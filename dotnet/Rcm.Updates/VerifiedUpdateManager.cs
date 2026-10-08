using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;
using Velopack.Sources;

namespace Rcm.Updates;

public sealed class VerifiedUpdateManager(IUpdateSource source, IVelopackLocator? locator = null)
    : UpdateManager(source, new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = false, MaximumDeltasBeforeFallback = 1 }, locator)
{
    private bool deferCleanup;
    protected override void CleanPackagesExcept(string? assetToKeep)
    {
        if (deferCleanup) return;
        if (assetToKeep is not null && !File.Exists(assetToKeep))
        {
            var installed = Locator.GetLatestLocalFullPackage();
            if (installed is not null && installed.Version == CurrentVersion)
                assetToKeep = Path.Combine(Locator.PackagesDir!, installed.FileName);
        }
        base.CleanPackagesExcept(assetToKeep);
    }

    protected override UpdateInfo CreateDeltaUpdateStrategy(VelopackAsset[] feed, VelopackAsset? latestLocalFull, VelopackAsset latestRemoteFull)
        => Source is SignedUpdateSource signed && signed.CanVerifyPackageContents(latestRemoteFull)
            ? base.CreateDeltaUpdateStrategy(feed, latestLocalFull, latestRemoteFull)
            : new UpdateInfo(latestRemoteFull, false);

    protected override async Task DownloadAndApplyDeltaUpdates(UpdateInfo updates, string targetFile, Action<int> progress, CancellationToken cancelToken)
    {
        if (Source is not SignedUpdateSource signed || !signed.CanVerifyPackageContents(updates.TargetFullRelease))
            throw new System.Security.Cryptography.CryptographicException("Delta reconstruction requires signed package contents.");
        await base.DownloadAndApplyDeltaUpdates(updates, targetFile, progress, cancelToken);
        await signed.VerifyPackageContentsAsync(updates.TargetFullRelease, targetFile, cancelToken);
    }

    private async Task VerifyAcceptedPackage(VelopackAsset target, string path, CancellationToken ct)
    {
        try { await VerifyPackageChecksumAsync(target, path); }
        catch (ChecksumFailedException) when (Source is SignedUpdateSource signed && signed.CanVerifyPackageContents(target))
        { await signed.VerifyPackageContentsAsync(target, path, ct); }
    }

    public async Task VerifyReadyUpdateAsync(UpdateInfo updates, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = updates.TargetFullRelease;
        var path = Path.Combine(Locator.PackagesDir!, target.FileName);
        if (!File.Exists(path)) throw new FileNotFoundException("Downloaded update package is missing.");
        await VerifyAcceptedPackage(target, path, ct);
        ct.ThrowIfCancellationRequested();
    }

    public override async Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress = null, CancellationToken cancelToken = default)
    {
        cancelToken.ThrowIfCancellationRequested();
        var target = updates.TargetFullRelease;
        var path = Path.Combine(Locator.PackagesDir!, target.FileName);
        if (File.Exists(path))
        {
            try { await VerifyAcceptedPackage(target, path, cancelToken); }
            catch (Exception error) when (error is ChecksumFailedException or System.Security.Cryptography.CryptographicException or InvalidDataException)
            { File.Delete(path); }
        }
        var installed = Locator.GetLatestLocalFullPackage();
        var basePath = installed is not null && installed.Version == CurrentVersion ? Path.Combine(Locator.PackagesDir!, installed.FileName) : null;
        var installedPath = Path.Combine(Locator.PackagesDir!, $"RCM-{CurrentVersion}-full.nupkg");
        if (basePath is null && File.Exists(installedPath)) basePath = installedPath;
        var accepted = false;
        deferCleanup = true;
        try
        {
            await base.DownloadUpdatesAsync(updates, progress, cancelToken);
            await VerifyAcceptedPackage(target, path, cancelToken);
            cancelToken.ThrowIfCancellationRequested();
            accepted = true;
        }
        finally
        {
            deferCleanup = false;
            if (!accepted) File.Delete(path);
            CleanPackagesExcept(accepted ? path : basePath);
        }
    }
}
