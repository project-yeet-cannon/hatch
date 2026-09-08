namespace Aerie.Hatch;

/// <summary>
/// Where the runner says things.
/// </summary>
/// <remarks>
/// An object rather than <c>Console</c> directly, for two reasons that are the
/// same reason. A test needs to read back what a pass printed - "the board is
/// busy" and "the board is empty" are different sentences and telling them apart
/// is an acceptance criterion - and the streamed lines of a session arrive on a
/// reader thread while the main one is writing its own, so somewhere has to hold
/// the lock that stops two lines landing on top of each other.
/// </remarks>
public class Terminal
{
    private readonly Lock _gate = new();

    /// <summary>What a person reads. Standard output.</summary>
    public virtual void Line(string line)
    {
        lock (_gate) Console.Out.WriteLine(line);
    }

    /// <summary>What went wrong. Standard error, so a redirected log keeps the two apart.</summary>
    public virtual void Complain(string line)
    {
        lock (_gate) Console.Error.WriteLine(line);
    }

    /// <summary>Several of them, in order.</summary>
    public void Lines(IEnumerable<string> lines)
    {
        foreach (var line in lines) Line(line);
    }
}

/// <summary>Everything said, in order, and nothing printed. What a test reads back.</summary>
public sealed class Transcript : Terminal
{
    private readonly Lock _gate = new();
    private readonly List<string> _said = [];
    private readonly List<string> _complained = [];

    public IReadOnlyList<string> Said
    {
        get { lock (_gate) return [.. _said]; }
    }

    public IReadOnlyList<string> Complained
    {
        get { lock (_gate) return [.. _complained]; }
    }

    /// <summary>Whether anything said anywhere contains this.</summary>
    public bool Mentions(string what)
    {
        lock (_gate) return _said.Concat(_complained).Any(l => l.Contains(what, StringComparison.Ordinal));
    }

    public override void Line(string line)
    {
        lock (_gate) _said.Add(line);
    }

    public override void Complain(string line)
    {
        lock (_gate) _complained.Add(line);
    }
}
