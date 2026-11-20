// Gapless playback between files: the engine is told which file comes next
// (pc_player_set_next) and continues into it in the same output stream; PC_EVENT_NEXT says
// when the join is heard, and the app then shows the next track. Files that would play
// differently, videos, and cue spans that don't start at the beginning of their file still
// reopen as before (the engine ends with PC_EVENT_EOF and Next() opens the next item).
using System;
using System.Linq;

namespace SurroundPlayer;

public sealed partial class PlayerModel
{
    private Track? _queuedNext;   // what the engine was told comes next

    private static readonly string[] VideoExtensions =
        { ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".webm", ".ts", ".m2ts", ".mts", ".wmv", ".mpg", ".mpeg", ".vob", ".flv" };

    /// Tell the engine the playlist's next item, when it can follow gaplessly.
    private void UpdateGaplessNext()
    {
        if (!_isOpen || Current == null) return;
        Track? next = null;
        int i = Playlist.IndexOf(Current);
        if (i >= 0 && i + 1 < Playlist.Count && Current.End == null && !HasVideo)
        {
            var n = Playlist[i + 1];
            bool fromStart = n.Cue == null || n.Cue.Start < 0.001;   // a cue span mid-file needs a seek: reopen
            bool otherFile = !string.Equals(n.Path, Current.Path, StringComparison.OrdinalIgnoreCase);
            bool video = VideoExtensions.Contains(System.IO.Path.GetExtension(n.Path), StringComparer.OrdinalIgnoreCase);
            if (fromStart && otherFile && !video) next = n;
        }
        if (next == _queuedNext) return;
        _queuedNext = next;
        Native.pc_player_set_next(_player, next?.Path);
        DebugTrace.Log($"gapless next: {next?.Name ?? "-"}");
    }

    /// PC_EVENT_NEXT: the queued track is now the one being heard.
    private void NextStarted(int generation)
    {
        if (generation != System.Threading.Volatile.Read(ref _generation) || _queuedNext == null) return;
        var track = _queuedNext;
        _queuedNext = null;
        DebugTrace.Log($"gapless: now playing {track.Name}");
        PrepareSubtitlesForOpen(track);
        Current = track;
        _resumeTrack = null;
        LoadInfo(track);
        ScheduleSave();
        UpdateGaplessNext();
    }
}
