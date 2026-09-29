namespace PairShare.Services;

public sealed record PairingTicket(string Code, string QuickToken, DateTimeOffset ExpiresAt, int Version);

public enum PairOutcome { Success, WrongCode, LockedOut }

public readonly record struct PairAttempt(PairOutcome Outcome, int AttemptsLeft = 0, TimeSpan RetryAfter = default, bool JustLocked = false);

/// <summary>
/// Owns the current pair code and quick-pair token. Both are single-use: a successful
/// pairing (or the lifetime running out) replaces them with fresh ones. Wrong guesses are
/// rate-limited per client, and a code that collects too many wrong guesses is replaced.
/// </summary>
public sealed class PairingService
{
    public const int CodeLength = 6;
    public const int MaxFailuresPerClient = 5;
    public const int MaxFailuresPerCode = 20;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<string, ClientAttempts> _clients = new(StringComparer.Ordinal);
    private string _code = "";
    private string _quickToken = "";
    private DateTimeOffset _expiresAt;
    private int _failuresOnCode;
    private int _version;

    public PairingService(AppOptions options, TimeProvider time)
    {
        _time = time;
        Lifetime = options.CodeLifetime;
        lock (_gate)
        {
            RotateLocked();
        }
    }

    /// <summary>Raised (outside the lock) whenever the code / quick token change.</summary>
    public event Action? Rotated;

    public TimeSpan Lifetime { get; }

    public PairingTicket Current
    {
        get
        {
            RotateIfExpired();
            lock (_gate)
            {
                return new PairingTicket(_code, _quickToken, _expiresAt, _version);
            }
        }
    }

    public void Regenerate()
    {
        lock (_gate)
        {
            RotateLocked();
        }

        Rotated?.Invoke();
    }

    public void RotateIfExpired()
    {
        lock (_gate)
        {
            if (_time.GetUtcNow() < _expiresAt)
            {
                return;
            }

            RotateLocked();
        }

        Rotated?.Invoke();
    }

    public PairAttempt TryCode(string clientKey, string? code)
    {
        var rotated = false;
        try
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                PruneLocked(now);

                if (now >= _expiresAt)
                {
                    RotateLocked();
                    rotated = true;
                }

                _clients.TryGetValue(clientKey, out var client);
                if (client is not null && client.LockedUntil > now)
                {
                    return new PairAttempt(PairOutcome.LockedOut, RetryAfter: client.LockedUntil - now);
                }

                var digits = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
                if (digits.Length == CodeLength && Tokens.FixedTimeEquals(digits, _code))
                {
                    _clients.Remove(clientKey);
                    RotateLocked();
                    rotated = true;
                    return new PairAttempt(PairOutcome.Success);
                }

                client ??= _clients[clientKey] = new ClientAttempts();
                client.Failures++;
                client.LastFailure = now;

                if (++_failuresOnCode >= MaxFailuresPerCode)
                {
                    RotateLocked();
                    rotated = true;
                }

                if (client.Failures >= MaxFailuresPerClient)
                {
                    client.Failures = 0;
                    client.LockedUntil = now + Lockout;
                    return new PairAttempt(PairOutcome.LockedOut, RetryAfter: Lockout, JustLocked: true);
                }

                return new PairAttempt(PairOutcome.WrongCode, AttemptsLeft: MaxFailuresPerClient - client.Failures);
            }
        }
        finally
        {
            if (rotated)
            {
                Rotated?.Invoke();
            }
        }
    }

    public bool TryQuickToken(string? token)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(token) || _time.GetUtcNow() >= _expiresAt || !Tokens.FixedTimeEquals(token, _quickToken))
            {
                return false;
            }

            RotateLocked();
        }

        Rotated?.Invoke();
        return true;
    }

    private void RotateLocked()
    {
        string next;
        do
        {
            next = Tokens.Digits(CodeLength);
        }
        while (next == _code);

        _code = next;
        _quickToken = Tokens.New(16);
        _expiresAt = _time.GetUtcNow() + Lifetime;
        _failuresOnCode = 0;
        _version++;
    }

    private void PruneLocked(DateTimeOffset now)
    {
        if (_clients.Count < 64)
        {
            return;
        }

        foreach (var (key, client) in _clients.ToArray())
        {
            if (client.LockedUntil <= now && now - client.LastFailure > TimeSpan.FromMinutes(10))
            {
                _clients.Remove(key);
            }
        }
    }

    private sealed class ClientAttempts
    {
        public int Failures;
        public DateTimeOffset LastFailure;
        public DateTimeOffset LockedUntil;
    }
}
