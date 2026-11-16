// Copyright (c) Duncan Lawler. All Rights Reserved.
using Xunit;

namespace PhotoLibrarian.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WaveletNoiseReductionExperimentCollection
{
    public const string Name = "Wavelet noise reduction experiments";
}
