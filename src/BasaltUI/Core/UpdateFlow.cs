namespace BasaltUI.Core;

/// <summary>
/// The decision behind "update basalt", kept away from processes and the file system so it can be argued
/// about without a server to kill: what is answering on the port, who started it, which process can be named
/// for it, and what the user therefore has to be asked. Windows will not let an exe be replaced from
/// underneath the process that is running it, so this is the whole of the question.
/// </summary>
public sealed record UpdatePlan(bool Answers, bool Ours, int Pid, int Port)
{
    /// <summary>Nothing is listening: the files can go in straight away.</summary>
    public bool Clear => !Answers;

    /// <summary>Our own child process is running: stop it, no ceremony needed.</summary>
    public bool StopOurs => Answers && Ours;

    /// <summary>Someone else started it, and can be named: stopping it is their call, so the app asks first.</summary>
    public bool AskFirst => Answers && !Ours && Pid != 0;

    /// <summary>Something answers that we did not start and no process can be named for it: nothing safe to touch.</summary>
    public bool Blocked => Answers && !Ours && Pid == 0;

    /// <summary>
    /// <paramref name="listeners" /> is whoever holds the server port. The first of them is the name worth
    /// showing; the rest of the tree is found again when the stop happens, not from this list.
    /// </summary>
    public static UpdatePlan Decide(bool answers, bool ours, int[]? listeners, int port)
        => new(answers, ours, listeners is { Length: > 0 } ? listeners[0] : 0, port);

    /// <summary>
    /// The question, worded for who owns the running server. Say plainly that a server this app did not start
    /// is the one being stopped — the alternative is a dialog that reads like a formality and kills something
    /// the user was typing into two seconds ago.
    /// </summary>
    public string Question(string newer)
        => Blocked
            ? $"Something is answering on port {Port}, but no process behind it can be named from here.\n\n"
              + "Stop that server yourself, then update again — its files cannot be replaced while it runs."
            : AskFirst
                ? $"A server that Basalt UI did not start is answering on port {Port} (pid {Pid}).\n\n"
                  + $"Its files cannot be replaced while it runs. Send it Ctrl+C — the polite stop basalt listens "
                  + $"for — put {newer} in place, and start it again from here?"
                : "basalt has to stop before its own files can be replaced.\n\n"
                  + $"Stop the server now, put {newer} in place, and start it again?";

    /// <summary>What was done about it, for the log pane and the tray balloon.</summary>
    public string Describe()
        => Blocked
            ? $"something answers on port {Port} with no process behind it that this app can name"
            : AskFirst
                ? $"the server on port {Port} (pid {Pid}, not started by Basalt UI)"
                : $"the server Basalt UI started (pid {Pid})";
}
