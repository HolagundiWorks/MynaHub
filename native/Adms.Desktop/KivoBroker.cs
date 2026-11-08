using Osas.Core.Broker;

namespace Adms.Desktop;

/// <summary>
/// Kivo's end of the session broker (docs/contracts/session-broker.md). It serves the pipe, tells the broker who is signed in, and
/// hands a sign-in request to the main window. All the protocol itself is in Osas.Core, where it is tested.
/// </summary>
internal sealed class KivoBroker : IBrokerHost, IDisposable
{
    private readonly object _gate = new();
    private readonly SessionBroker _broker;
    private SessionBrokerPipe? _pipe;
    private BrokerIdentity? _identity;
    private BrokerIdentity? _last;
    private DateTime _signedInUtc;
    private TaskCompletionSource<BrokerIdentity?>? _pending;

    internal KivoBroker() => _broker = new SessionBroker(this);

    /// <summary>Set by the main window: bring Kivo to the front on its sign-in. Called from any thread.</summary>
    internal Action? ShowSignIn { get; set; }

    public BrokerIdentity? CurrentIdentity
    {
        get
        {
            lock (_gate)
                return _identity;
        }
    }

    public DateTime SignedInUtc
    {
        get
        {
            lock (_gate)
                return _signedInUtc;
        }
    }

    internal bool SignInPending
    {
        get
        {
            lock (_gate)
                return _pending != null;
        }
    }

    /// <summary>Creates the pipe. False when another Kivo already serves it.</summary>
    internal bool Start()
    {
        _pipe = new SessionBrokerPipe(_broker);
        return _pipe.Start();
    }

    public Task<BrokerIdentity?> RequestSignInAsync(CancellationToken cancel)
    {
        Task<BrokerIdentity?> wait;
        bool first;
        lock (_gate)
        {
            if (_identity != null)
                return Task.FromResult<BrokerIdentity?>(_identity);
            first = _pending == null;
            _pending ??= new TaskCompletionSource<BrokerIdentity?>(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = _pending.Task;
        }
        if (first)
            ShowSignIn?.Invoke();
        return wait.WaitAsync(cancel);
    }

    /// <summary>The person signed in (called by the main window). Answers any caller that was waiting.</summary>
    internal void SignedIn(BrokerIdentity who)
    {
        TaskCompletionSource<BrokerIdentity?>? waiting;
        lock (_gate)
        {
            // Someone else, or another company, must not inherit tokens given to the person before.
            if (_last != null && (_last.User != who.User || _last.CompanyId != who.CompanyId))
                _broker.EndAllSessions();
            _last = who;
            _identity = who;
            if (_signedInUtc == default)
                _signedInUtc = DateTime.UtcNow;
            waiting = _pending;
            _pending = null;
        }
        waiting?.TrySetResult(who);
    }

    /// <summary>The session is locked: no new tokens until it is unlocked, but the ones given stay.</summary>
    internal void Locked()
    {
        lock (_gate)
            _identity = null;
    }

    /// <summary>Signed out, or the company was closed: every token ends.</summary>
    internal void SignedOut()
    {
        lock (_gate)
        {
            _identity = null;
            _last = null;
            _signedInUtc = default;
        }
        _broker.EndAllSessions();
    }

    /// <summary>The person closed the sign-in without signing in: waiting callers get <c>cancelled</c>.</summary>
    internal void CancelSignIn()
    {
        TaskCompletionSource<BrokerIdentity?>? waiting;
        lock (_gate)
        {
            waiting = _pending;
            _pending = null;
        }
        waiting?.TrySetResult(null);
    }

    public void Dispose()
    {
        CancelSignIn();
        _pipe?.Dispose();
    }
}
