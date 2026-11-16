// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;
using AppDebugLog = PhotoLibrarian.Diagnostics.DebugLog;
using CoreDebugLog = PhotoLibrarian.Core.Diagnostics.DebugLog;

namespace PhotoLibrarian.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GridDiagnosticCollection
{
    public const string Name = "Grid diagnostics";
}

[Collection(GridDiagnosticCollection.Name)]
public sealed class GridDiagnosticTests
{
    [Theory]
    [InlineData("VirtualizingPhotoGrid.xaml.cs")]
    [InlineData("ImageGridView.xaml.cs")]
    public void GridDiagnosticsNeverWriteDirectlyToDebugger(string fileName)
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", fileName));

        Assert.Contains("DebugLog.WriteDebugLine(", source);
        Assert.DoesNotMatch(new Regex(@"\b(?:Debug|Trace)\s*\.\s*(?:Write|WriteLine|Print)\s*\("), source);
        Assert.DoesNotMatch(new Regex(@"\bOutputDebugString\w*\s*\("), source);
    }

    [Fact]
    public void VerboseGridDiagnosticsRemainDebugOnly()
    {
        var method = typeof(AppDebugLog).GetMethod(nameof(AppDebugLog.WriteDebugLine))!;

        Assert.Equal("DEBUG", method.GetCustomAttribute<ConditionalAttribute>()?.ConditionString);
    }

    [Fact]
    public void VerboseDiagnosticsEvaluateMessagesOnlyInDebugBuilds()
    {
        bool evaluated = false;

        AppDebugLog.WriteDebugLine(CreateMessage());

#if DEBUG
        Assert.True(evaluated);
#else
        Assert.False(evaluated);
#endif

        string CreateMessage()
        {
            evaluated = true;
            return "[VIRTUAL] Conditional diagnostic regression";
        }
    }

    [Fact]
    public void GridDiagnosticsReturnEvenWhenWriterIsBlockedAndQueueIsFull()
    {
        // Stall the existing sink without touching a debugger, log file, or user profile.
        var fileLock = typeof(CoreDebugLog)
            .GetField("FileLock", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var pending = (BlockingCollection<string>)typeof(CoreDebugLog)
            .GetField("Pending", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var write = typeof(AppDebugLog).GetMethod(nameof(AppDebugLog.WriteDebugLine))!
            .CreateDelegate<Action<string>>();
        bool wasEnabled = AppDebugLog.EnableLogging;
        Exception? producerFailure = null;
        var producer = new Thread(ProduceDiagnostics)
        {
            IsBackground = true,
            Name = "Grid diagnostic regression producer"
        };

        try
        {
            AppDebugLog.EnableLogging = true;
            lock (fileLock)
            {
                producer.Start();
                Assert.True(producer.Join(TimeSpan.FromSeconds(5)),
                    "Grid diagnostics waited for the stalled background sink.");
                Assert.Null(producerFailure);
                Assert.Equal(pending.BoundedCapacity, pending.Count);
            }
        }
        finally
        {
            // Release the sink before joining even if a blocking-queue regression fails the test.
            producer.Join(TimeSpan.FromSeconds(10));
            AppDebugLog.EnableLogging = wasEnabled;
        }

        void ProduceDiagnostics()
        {
            try
            {
                for (int i = 0; i < pending.BoundedCapacity + 2; i++)
                    write("[VIRTUAL] Diagnostic backpressure regression");
            }
            catch (Exception exception)
            {
                producerFailure = exception;
            }
        }
    }
}
