namespace GirlsMadeInfinitePudding.ProcessMemory;

/// <summary>
///     Raised by <see cref="GameProcess" /> when any primitive that crosses the
///     process boundary fails — <c>ReadProcessMemory</c>, <c>WriteProcessMemory</c>,
///     <c>CreateRemoteThread</c>, <c>WaitForSingleObject</c>,
///     <c>GetExitCodeThread</c>, <c>VirtualAllocEx</c>, <c>VirtualFreeEx</c>.
///     <para>
///         A dedicated exception type lets the UI distinguish "remote side misbehaved /
///         died" from ordinary bugs (argument validation, logic errors) so that a dead
///         game process can trigger a silent <see cref="GirlsMadeInfinitePudding.TrainerViewModel.Disconnect" />
///         instead of a scary red toast with a stack trace.
///     </para>
/// </summary>
public sealed class RemoteInvocationException : Exception
{
    public RemoteInvocationException(string message, int win32Error, Exception? inner = null)
        : base(message, inner)
    {
        Win32Error = win32Error;
    }

    public int Win32Error { get; }
}