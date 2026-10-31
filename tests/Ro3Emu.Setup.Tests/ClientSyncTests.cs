using Ro3Emu.ClientRelease;
using Ro3Emu.Setup;

namespace Ro3Emu.Setup.Tests;

public sealed class ClientSyncTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly GenesisIdentity Current = new(140083, "2026.10.30.1", "build", Hash, Hash);
    private static ClientReleaseRequirement Requirement(GenesisIdentity? identity = null, long sequence = 2) => new()
    {
        ServerVersion = "1.0.24", BaseClient = identity ?? Current, Channel = "stable", Sequence = sequence,
        Version = "1.0.24", ManifestSha256 = Hash, PatchSha256 = Hash
    };

    private sealed class Flow
    {
        public readonly List<string> Events = [];
        public GenesisIdentity? Identity = Current with { Sequence = 140082 };
        public ClientReleaseRequirement? Published;
        public bool FailUpdate, FailSources, FailData, FailPublish, WrongCandidate;
        public ClientSyncOperations Steps => new()
        {
            ReadIdentity = () => Identity,
            UpdateOfficial = (_, _) => { Events.Add("update"); if (FailUpdate) throw new IOException("offline"); Identity = Current; return Task.CompletedTask; },
            ValidateSources = () => { Events.Add("sha"); if (FailSources) throw new IOException("bad SHA"); },
            Repatch = () => Events.Add("patch"),
            ValidateData = () => { Events.Add("data"); if (FailData) throw new InvalidDataException("wrong data"); },
            PatchFingerprint = () => Hash,
            ReadPublished = () => Published,
            PrepareCandidate = (_, _, _) => { Events.Add("publish"); if (FailPublish) throw new IOException("disk full"); return Task.FromResult(Requirement(WrongCandidate ? Current with { Sequence = 1 } : null)); },
            ActivateCandidate = () => Events.Add("activate"),
        };
        public async Task Start()
        {
            await ClientSyncCoordinator.PrepareAsync(new("1.0.24", 140083, "2026.10.30.1"), "1.0.24+test", Steps);
            Events.Add("start");
        }
    }

    [Fact]
    public async Task WrongGenesisUpdatesBeforeShaPatchDataPublishAndStart()
    {
        var flow = new Flow(); await flow.Start();
        Assert.Equal(["update", "sha", "patch", "data", "publish", "activate", "start"], flow.Events);
    }
    [Fact]
    public async Task MatchingBasePatchAndServerReusesSignedPublication()
    {
        var flow = new Flow { Identity = Current, Published = Requirement() }; await flow.Start();
        Assert.Equal(["sha", "patch", "data", "start"], flow.Events);
    }
    [Theory]
    [InlineData("update")]
    [InlineData("sha")]
    [InlineData("data")]
    [InlineData("publish")]
    [InlineData("candidate")]
    public async Task EveryFailureHoldsActivationAndServer(string at)
    {
        var f = new Flow { FailUpdate = at == "update", FailSources = at == "sha", FailData = at == "data", FailPublish = at == "publish", WrongCandidate = at == "candidate" };
        await Assert.ThrowsAnyAsync<Exception>(f.Start);
        Assert.DoesNotContain("activate", f.Events); Assert.DoesNotContain("start", f.Events);
    }
    [Fact]
    public async Task WrongServerSpecRefusesBeforeAnyMutation()
    {
        var f = new Flow();
        await Assert.ThrowsAsync<InvalidDataException>(() => ClientSyncCoordinator.PrepareAsync(new("1.0.23", 140083), "1.0.24", f.Steps));
        Assert.Empty(f.Events);
    }
    [Theory]
    [InlineData(140084, "2026.10.31.1")]
    [InlineData(140083, "wrong-release")]
    public void OfficialLatestMustMatchPinnedReleaseBeforePlanningWrites(long seq, string id) =>
        Assert.Throws<InvalidDataException>(() => new ClientCompatibilitySpec("1.0.24", 140083, "2026.10.30.1").ValidateOffered(seq, id, "build"));

    [Fact]
    public void MissingCandidateLeavesExistingPublicationInPlace()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3-sync-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(root, "repo"); Directory.CreateDirectory(repo); File.WriteAllText(Path.Combine(repo, "old"), "original");
        try { Assert.Throws<DirectoryNotFoundException>(() => ClientSync.Activate(repo, Path.Combine(root, ".repo.candidate-" + Guid.NewGuid().ToString("N")))); Assert.Equal("original", File.ReadAllText(Path.Combine(repo, "old"))); }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void StrictColdClientUsesExplicitEmptyDirectoryWithoutSearchingOrInstallingLatest()
    {
        string selected = Path.Combine(Path.GetTempPath(), "ro3-not-installed-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(Path.GetFullPath(selected), Flows.SelectPairedClient(selected, "other-client"));
        Assert.False(Directory.Exists(selected));
    }
    [Fact]
    public void StrictColdClientCanUseRememberedMissingDirectory()
    {
        string selected = Path.Combine(Path.GetTempPath(), "ro3-remembered-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(Path.GetFullPath(selected), Flows.SelectPairedClient(null, selected));
        Assert.False(Directory.Exists(selected));
    }
    [Fact]
    public void StrictMissingClientPathRefusesInsteadOfAutodetectingAnUnpairedClient() =>
        Assert.Throws<SetupException>(() => Flows.SelectPairedClient(null, null));
}
