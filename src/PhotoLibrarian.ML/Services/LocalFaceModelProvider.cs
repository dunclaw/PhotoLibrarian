// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.ML.Services;

/// <summary>Interactive Fuse never downloads a model or sends photos anywhere.</summary>
public sealed class LocalFaceModelProvider(OnnxSessionManager sessions) : IFaceModelProvider
{
    public async Task EnsureModelsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var model in FaceModelCatalog.All)
        {
            if (!await FaceModelProvider.HasExpectedHashAsync(
                Path.Combine(sessions.ModelDirectory, model.FileName), model, cancellationToken))
                throw new FaceModelsUnavailableException("Local face scanning is not available yet. " +
                    "Set up the app's existing face detection models, or add missed faces with Add people tags. " +
                    "Photo Fuse does not download models.");
        }
    }
}

public sealed class FaceModelsUnavailableException(string message) : InvalidOperationException(message);
