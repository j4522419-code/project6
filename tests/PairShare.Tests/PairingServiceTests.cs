using PairShare.Services;
using Xunit;

namespace PairShare.Tests;

public class PairingServiceTests
{
    private readonly ManualTime _time = new();

    private PairingService Create() => new(new AppOptions { CodeLifetime = TimeSpan.FromMinutes(5) }, _time);

    [Fact]
    public void Code_is_six_digits()
    {
        var code = Create().Current.Code;

        Assert.Equal(PairingService.CodeLength, code.Length);
        Assert.All(code, c => Assert.True(char.IsAsciiDigit(c)));
    }

    [Fact]
    public void Correct_code_pairs_once_then_rotates()
    {
        var pairing = Create();
        var rotations = 0;
        pairing.Rotated += () => rotations++;
        var ticket = pairing.Current;

        Assert.Equal(PairOutcome.Success, pairing.TryCode("a", ticket.Code).Outcome);
        Assert.NotEqual(ticket.Code, pairing.Current.Code);
        Assert.NotEqual(ticket.QuickToken, pairing.Current.QuickToken);
        Assert.Equal(1, rotations);
        Assert.Equal(PairOutcome.WrongCode, pairing.TryCode("b", ticket.Code).Outcome);
    }

    [Fact]
    public void Spaces_and_dashes_in_the_code_are_ignored()
    {
        var pairing = Create();
        var code = pairing.Current.Code;

        Assert.Equal(PairOutcome.Success, pairing.TryCode("a", $" {code[..3]}-{code[3..]} ").Outcome);
    }

    [Fact]
    public void Too_many_wrong_codes_locks_the_client_out_for_a_while()
    {
        var pairing = Create();
        for (var i = 1; i < PairingService.MaxFailuresPerClient; i++)
        {
            var attempt = pairing.TryCode("a", "not it");
            Assert.Equal(PairOutcome.WrongCode, attempt.Outcome);
            Assert.Equal(PairingService.MaxFailuresPerClient - i, attempt.AttemptsLeft);
        }

        Assert.Equal(PairOutcome.LockedOut, pairing.TryCode("a", "not it").Outcome);

        // Even the right code is refused while locked out...
        Assert.Equal(PairOutcome.LockedOut, pairing.TryCode("a", pairing.Current.Code).Outcome);

        // ...other clients are unaffected...
        Assert.Equal(PairOutcome.WrongCode, pairing.TryCode("b", "nope").Outcome);

        // ...and the lock expires.
        _time.Advance(PairingService.Lockout + TimeSpan.FromSeconds(1));
        Assert.Equal(PairOutcome.Success, pairing.TryCode("a", pairing.Current.Code).Outcome);
    }

    [Fact]
    public void A_code_that_collects_many_wrong_guesses_is_replaced()
    {
        var pairing = Create();
        var original = pairing.Current.Code;
        var wrong = original == "000000" ? "111111" : "000000";

        for (var i = 0; i < PairingService.MaxFailuresPerCode; i++)
        {
            pairing.TryCode($"client-{i}", wrong);
        }

        Assert.NotEqual(original, pairing.Current.Code);
    }

    [Fact]
    public void Codes_expire_after_their_lifetime()
    {
        var pairing = Create();
        var ticket = pairing.Current;

        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(PairOutcome.WrongCode, pairing.TryCode("a", ticket.Code).Outcome);
        Assert.False(pairing.TryQuickToken(ticket.QuickToken));
        Assert.True(pairing.Current.Version > ticket.Version);
    }

    [Fact]
    public void Quick_pair_token_is_single_use()
    {
        var pairing = Create();
        var token = pairing.Current.QuickToken;

        Assert.True(pairing.TryQuickToken(token));
        Assert.False(pairing.TryQuickToken(token));
        Assert.False(pairing.TryQuickToken(null));
        Assert.False(pairing.TryQuickToken(""));
    }

    [Fact]
    public void Regenerate_replaces_code_and_token()
    {
        var pairing = Create();
        var before = pairing.Current;

        pairing.Regenerate();

        Assert.NotEqual(before.Code, pairing.Current.Code);
        Assert.NotEqual(before.QuickToken, pairing.Current.QuickToken);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
