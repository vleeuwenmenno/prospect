using Prospect.Client.World;
using Prospect.Game.Protocol;

namespace Prospect.Client.Screens;

/// <summary>Writes the sentence for an edit the server refused or ended; <paramref name="kept"/> adds that the draft is kept.</summary>
internal delegate ReadOnlySpan<byte> EditRefusalText(EditorReason reason, int wait, bool kept);

/// <summary>
/// Turns what the ship editor reports into notices, once each: an edit applied (a ship built or rebuilt), an edit the
/// server refused or ended, the refusal to take control of a built ship. The request waiting on the server is one
/// notice, posted only if the wait lasts <see cref="AskingDelay"/> and withdrawn when the server answers. The editor
/// stamps each outcome with its own clock, so a changed stamp is a new event. The first update after
/// <see cref="Reset"/> takes the state as it is, so nothing left over is news. No fonts, no allocation.
/// </summary>
internal sealed class EditorNoticeTracker
{
    /// <summary>Seconds a request must wait for the server before its notice is posted, so a quick grant shows none.</summary>
    public const float AskingDelay = 0.4f;

    private bool _primed;
    private float _noticeAt = float.NegativeInfinity;
    private float _followAt = float.NegativeInfinity;
    private float _askingSince = float.NegativeInfinity;
    private bool _askingPosted;

    /// <summary>Forgets what the editor last reported: a new session starts with nothing to announce.</summary>
    public void Reset()
    {
        _primed = false;
        _askingSince = float.NegativeInfinity;
        _askingPosted = false;
    }

    /// <summary>Follows one frame of <paramref name="editor"/>, posting to <paramref name="board"/>.</summary>
    public void Update(NoticeBoard board, ShipEditor editor, EditRefusalText refusal)
    {
        var primed = _primed;
        var noticeNew = editor.NoticeAt != _noticeAt;
        var followNew = editor.FollowNoticeAt != _followAt;
        _primed = true;
        _noticeAt = editor.NoticeAt;
        _followAt = editor.FollowNoticeAt;

        if (editor.Stage != EditorStage.Requesting)
        {
            if (_askingPosted)
                board.Withdraw(NoticeKinds.EditAsking);
            _askingPosted = false;
            _askingSince = float.NegativeInfinity;
        }
        else if (!_askingPosted)
        {
            if (_askingSince == float.NegativeInfinity)
                _askingSince = editor.Time;
            if (editor.Time - _askingSince >= AskingDelay)
            {
                board.Post(NoticeKinds.EditAsking);
                _askingPosted = true;
            }
        }

        if (!primed)
            return;

        if (followNew && editor.FollowNotice != FollowRefusal.None)
            board.Post(editor.FollowNotice == FollowRefusal.OutOfSector ? NoticeKinds.FollowOutOfSector : NoticeKinds.FollowMissing);

        if (!noticeNew)
            return;
        if (editor.NoticeApplied)
            board.Post(editor.NoticeBuilt ? NoticeKinds.ShipFlown : NoticeKinds.ShipRebuilt);
        else if (editor.Notice is not (EditorReason.None or EditorReason.Cancelled))
            board.Post(NoticeKinds.EditRefused, refusal(editor.Notice, editor.NoticeWait, editor.DraftKept));
    }
}
