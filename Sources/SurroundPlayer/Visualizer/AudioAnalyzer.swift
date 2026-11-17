import Accelerate
import Foundation

/// Turns the engine's audio tap into what presets react to: smoothed waveforms,
/// a spectrum, and MilkDrop-style relative band levels (`bass` ≈ 1 is "normal for
/// this song", 2 is a hit) with slower `…Att` versions.
final class AudioAnalyzer {
    static let fftSize = 1024
    private let log2n = vDSP_Length(10)
    private let setup: FFTSetup
    private var raw = [Float](repeating: 0, count: AudioAnalyzer.fftSize * 2)
    private var window = [Float](repeating: 0, count: AudioAnalyzer.fftSize)
    private var mono = [Float](repeating: 0, count: AudioAnalyzer.fftSize)
    private var re = [Float](repeating: 0, count: AudioAnalyzer.fftSize / 2)
    private var im = [Float](repeating: 0, count: AudioAnalyzer.fftSize / 2)

    /// 512 points each, roughly -1…1
    private(set) var left = [Float](repeating: 0, count: 512)
    private(set) var right = [Float](repeating: 0, count: 512)
    /// 512 magnitude bins (0…24 kHz), log-compressed to roughly 0…1
    private(set) var spectrum = [Float](repeating: 0, count: 512)

    private(set) var bass: Float = 1, mid: Float = 1, treb: Float = 1
    private(set) var bassAtt: Float = 1, midAtt: Float = 1, trebAtt: Float = 1
    private(set) var volume: Float = 0
    private(set) var beat = false
    private(set) var hasSignal = false

    private var shortE: [Float] = [0, 0, 0]
    private var longE: [Float] = [0, 0, 0]
    private var lastBeat: Double = 0

    init() {
        setup = vDSP_create_fftsetup(10, FFTRadix(kFFTRadix2))!
        vDSP_hann_window(&window, vDSP_Length(Self.fftSize), Int32(vDSP_HANN_NORM))
    }

    deinit { vDSP_destroy_fftsetup(setup) }

    /// `fetch` fills interleaved L/R floats and returns frames written (0 = nothing playing).
    func update(dt: Float, time: Double, fetch: (UnsafeMutablePointer<Float>, Int) -> Int) {
        let n = Self.fftSize
        let got = raw.withUnsafeMutableBufferPointer { fetch($0.baseAddress!, n) }
        hasSignal = got == n
        if !hasSignal {
            // decay gracefully to silence
            for i in 0..<512 { left[i] *= 0.85; right[i] *= 0.85; spectrum[i] *= 0.9 }
            bass += (1 - bass) * 0.1; mid += (1 - mid) * 0.1; treb += (1 - treb) * 0.1
            bassAtt = bass; midAtt = mid; trebAtt = treb
            volume *= 0.9
            beat = false
            return
        }
        // waveforms: decimate 1024 -> 512 with a little smoothing
        for i in 0..<512 {
            let a = i * 4
            left[i] = left[i] * 0.35 + 0.65 * (raw[a] + raw[a + 2]) * 0.5
            right[i] = right[i] * 0.35 + 0.65 * (raw[a + 1] + raw[a + 3]) * 0.5
        }
        // spectrum
        for i in 0..<n { mono[i] = (raw[i * 2] + raw[i * 2 + 1]) * 0.5 * window[i] }
        re.withUnsafeMutableBufferPointer { reP in
            im.withUnsafeMutableBufferPointer { imP in
                var split = DSPSplitComplex(realp: reP.baseAddress!, imagp: imP.baseAddress!)
                mono.withUnsafeBufferPointer { m in
                    m.baseAddress!.withMemoryRebound(to: DSPComplex.self, capacity: n / 2) {
                        vDSP_ctoz($0, 2, &split, 1, vDSP_Length(n / 2))
                    }
                }
                vDSP_fft_zrip(setup, &split, 1, log2n, FFTDirection(FFT_FORWARD))
                imP[0] = 0 // packed Nyquist term
                spectrum.withUnsafeMutableBufferPointer { sp in
                    vDSP_zvabs(&split, 1, sp.baseAddress!, 1, vDSP_Length(n / 2))
                }
            }
        }
        var energy: [Float] = [0, 0, 0]
        for i in 1..<512 {
            let m = spectrum[i] / Float(n)
            let e = m * m
            // 46.9 Hz bins: bass < ~280 Hz, mid < ~2.3 kHz, treble < ~8 kHz
            if i <= 6 { energy[0] += e } else if i <= 48 { energy[1] += e } else if i <= 170 { energy[2] += e }
            spectrum[i] = min(1, log10(1 + m * 400) * 0.5)
        }
        spectrum[0] = 0
        var vol: Float = 0
        for i in 0..<n { vol += raw[i * 2] * raw[i * 2] }
        volume = min(2, sqrt(vol / Float(n)) * 4)

        // relative levels
        let fast = min(1, dt * 18), slow = min(1, dt * 0.35), att = min(1, dt * 2.5)
        var rel: [Float] = [1, 1, 1]
        for b in 0..<3 {
            shortE[b] += (energy[b] - shortE[b]) * fast
            if longE[b] < 1e-9 { longE[b] = energy[b] } // first signal: start at "normal"
            longE[b] += (energy[b] - longE[b]) * slow
            rel[b] = longE[b] > 1e-9 ? min(4, sqrt(shortE[b] / longE[b])) : 1
        }
        bass = rel[0]; mid = rel[1]; treb = rel[2]
        bassAtt += (bass - bassAtt) * att
        midAtt += (mid - midAtt) * att
        trebAtt += (treb - trebAtt) * att
        beat = bass > 1.45 && bass > bassAtt * 1.25 && time - lastBeat > 0.25
        if beat { lastBeat = time }
    }
}
