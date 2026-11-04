namespace Matinee.Room;

public enum LineKind : byte { Say, System, Clash }

/// <summary>A chat participant, as the floor sees one.</summary>
public sealed class ChatUser(int id, string nick)
{
    public int Id { get; } = id;
    public string Nick { get; } = nick;

    /// <summary>
    /// Text the server has taken from this user's input box (sent them "took") that their client may not have
    /// removed yet. Incoming input is normalized against it; see <see cref="Floor.Normalize"/>.
    /// </summary>
    internal readonly List<string> PendingTook = new();
}

public interface IFloorSink
{
    /// <summary>A history line was spoken (by a user, or by the system when nick is null).</summary>
    void Line(ChatUser? user, string text, LineKind kind);
    /// <summary>Tell <paramref name="user"/> that <paramref name="text"/> left their input box and was spoken.</summary>
    void Took(ChatUser user, string text);
    /// <summary>The live line, typing or wanting sets changed.</summary>
    void StateChanged();
}

/// <summary>
/// The movie-theater chat: one live line, contended for like a room full of people talking.
///
/// <list type="bullet">
/// <item>The live line is free until someone completes a word (space). That person takes the floor; their
/// words and corrections stream to everyone as they type.</item>
/// <item>Anyone else who types while the floor is taken keeps their text in their own box and is listed as
/// wanting to speak. Enter sends it, but only when the floor is free.</item>
/// <item>The owner's line is committed to history on Enter, or after <see cref="IdleMs"/> of silence.</item>
/// <item>If someone keeps talking over the owner for <see cref="ClashWords"/> words, it turns into a shouting
/// match: the line so far is committed, and from then on every word either of them completes becomes its own
/// history line, interleaved. It lasts until the floor is released; someone has to shut up.</item>
/// </list>
///
/// Not thread-safe: the room serializes calls.
/// </summary>
public sealed class Floor(IFloorSink sink)
{
    public const int ClashWords = 8;
    public const double IdleMs = 2500;
    public const int MaxText = 500;

    private sealed class Contender { public int Words; public int Overlap; }

    private readonly Dictionary<ChatUser, Contender> _contenders = new();
    private readonly HashSet<ChatUser> _clashing = new();
    private readonly HashSet<ChatUser> _typing = new();
    private readonly List<ChatUser> _wanting = new();
    private double _lastOwnerInput;

    public ChatUser? Owner { get; private set; }
    public string LiveText { get; private set; } = "";
    public bool Clash => _clashing.Count > 0;
    public IReadOnlyCollection<ChatUser> Typing => _typing;
    public IReadOnlyList<ChatUser> Wanting => _wanting;
    public bool IsClashing(ChatUser u) => _clashing.Contains(u);

    public void SetTyping(ChatUser u, bool on)
    {
        bool changed = on ? _typing.Add(u) : _typing.Remove(u);
        if (!on) changed |= _wanting.Remove(u);
        if (changed) sink.StateChanged();
    }

    public void Input(ChatUser u, string text, InputKind kind, double now)
    {
        if (text.Length > MaxText) text = text[..MaxText];
        text = Normalize(u, text);

        if (Owner is null)
        {
            if (kind == InputKind.Send)
            {
                SpeakAll(u, text);
                return;
            }
            if (kind == InputKind.Word && CompleteWords(text) > 0)
            {
                Owner = u;
                LiveText = text;
                _lastOwnerInput = now;
                _wanting.Remove(u);
                _typing.Add(u);
                _contenders.Clear();
                sink.StateChanged();
            }
            return;
        }

        if (Owner == u)
        {
            _lastOwnerInput = now;
            if (IsClashing(u))
            {
                if (kind == InputKind.Send) { SpeakWords(u, text, all: true); Release(); }
                else if (kind == InputKind.Word) SpeakWords(u, text, all: false);
                return;
            }
            if (kind == InputKind.Send)
            {
                LiveText = text;
                CommitLive();
                Release();
                return;
            }
            if (LiveText != text)
            {
                LiveText = text;
                sink.StateChanged();
            }
            return;
        }

        // Someone else holds the floor.
        if (kind == InputKind.Edit) return;
        if (IsClashing(u))
        {
            if (kind == InputKind.Word) SpeakWords(u, text, all: false);
            else SpeakWords(u, text, all: true);
            return;
        }
        bool changed = false;
        if (!_wanting.Contains(u)) { _wanting.Add(u); changed = true; }
        changed |= _typing.Add(u);
        if (kind == InputKind.Word)
        {
            int words = CompleteWords(text);
            if (!_contenders.TryGetValue(u, out var c))
            {
                c = new Contender { Words = words, Overlap = words > 0 ? 1 : 0 };
                _contenders[u] = c;
            }
            else if (words > c.Words)
            {
                c.Overlap += words - c.Words;
                c.Words = words;
            }
            else c.Words = words;

            if (c.Overlap >= ClashWords)
            {
                StartClash(u, text);
                return;
            }
        }
        if (changed) sink.StateChanged();
    }

    /// <summary>Release the floor if its owner went quiet.</summary>
    public void Tick(double now)
    {
        if (Owner is null || now - _lastOwnerInput < IdleMs) return;
        if (!IsClashing(Owner)) CommitLive();
        Release();
    }

    public void Leave(ChatUser u)
    {
        bool changed = _typing.Remove(u) | _wanting.Remove(u) | _contenders.Remove(u) | _clashing.Remove(u);
        if (Owner == u)
        {
            if (!Clash) CommitLive();
            Release();
            return;
        }
        if (changed) sink.StateChanged();
    }

    private void StartClash(ChatUser contender, string text)
    {
        var owner = Owner!;
        // Whatever each of them said so far is said; from now on, word by word.
        if (!IsClashing(owner))
        {
            CommitLive();
            _clashing.Add(owner);
        }
        _clashing.Add(contender);
        _contenders.Remove(contender);
        _wanting.Remove(contender);
        string done = CompletePart(text);
        if (done.Length > 0)
        {
            sink.Line(contender, done.Trim(), LineKind.Clash);
            Took(contender, done);
        }
        sink.StateChanged();
    }

    private void CommitLive()
    {
        var owner = Owner!;
        string t = LiveText.Trim();
        if (t.Length > 0)
        {
            sink.Line(owner, t, LineKind.Say);
            Took(owner, LiveText);
        }
        LiveText = "";
    }

    private void Release()
    {
        if (Owner is not null) _typing.Remove(Owner);
        Owner = null;
        LiveText = "";
        _clashing.Clear();
        _contenders.Clear();
        sink.StateChanged();
    }

    private void SpeakAll(ChatUser u, string text)
    {
        string t = text.Trim();
        if (t.Length == 0) return;
        sink.Line(u, t, LineKind.Say);
        Took(u, text);
        _wanting.Remove(u);
        _typing.Remove(u);
        sink.StateChanged();
    }

    /// <summary>Clash mode: each completed word (or every word, on Enter) becomes its own line.</summary>
    private void SpeakWords(ChatUser u, string text, bool all)
    {
        string part = all ? text : CompletePart(text);
        foreach (var w in part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            sink.Line(u, w, LineKind.Clash);
        if (part.Trim().Length > 0) Took(u, part);
    }

    private void Took(ChatUser u, string text)
    {
        string t = text.TrimEnd();
        if (t.Length == 0) return;
        if (u.PendingTook.Count >= 32) u.PendingTook.RemoveAt(0);
        u.PendingTook.Add(t);
        sink.Took(u, t);
    }

    /// <summary>
    /// The client sends its whole input box. After a "took", it strips the taken prefix, but input it sent before
    /// receiving that may still carry it. Strip what's still there, and forget a took once the client's input no
    /// longer starts with it (it was applied). Tooks are applied in order, each relative to the previous result.
    /// </summary>
    internal static string Normalize(ChatUser u, string text)
    {
        var pending = u.PendingTook;
        int i = 0;
        while (i < pending.Count)
        {
            string p = pending[i];
            string trimmed = text.TrimStart();
            if (trimmed.StartsWith(p, StringComparison.Ordinal))
            {
                text = trimmed[p.Length..];
                i++;
            }
            else pending.RemoveAt(i);
        }
        return text.TrimStart();
    }

    /// <summary>Words followed by whitespace (the last word is still being typed unless the text ends in a space).</summary>
    internal static int CompleteWords(string text)
    {
        int n = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { if (inWord) n++; inWord = false; }
            else inWord = true;
        }
        return n;
    }

    /// <summary>The prefix of <paramref name="text"/> made of complete words.</summary>
    internal static string CompletePart(string text)
    {
        int end = text.Length;
        while (end > 0 && !char.IsWhiteSpace(text[end - 1])) end--;
        return text[..end];
    }
}

public enum InputKind : byte { Word, Edit, Send }
