using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Midora.Application.Tests;

[SupportedOSPlatform("windows")]
public sealed class SingleApplicationInstanceCoordinatorTests
{
    private const string ChildModeEnvironmentVariable = "MIDORA_INSTANCE_TEST_CHILD";
    private const string ChildApplicationIdEnvironmentVariable = "MIDORA_INSTANCE_TEST_APP_ID";
    private const string ChildReadyPathEnvironmentVariable = "MIDORA_INSTANCE_TEST_READY_PATH";
    private const string ChildReceivedPathEnvironmentVariable = "MIDORA_INSTANCE_TEST_RECEIVED_PATH";

    [Fact]
    public async Task SecondaryLaunchForwardsExactStartupRequestAndMustExit()
    {
        string applicationId = NewApplicationId();
        ApplicationStartupRequest primaryRequest = Request("primary.midora");
        ApplicationInstanceStartResult primaryResult =
            await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                applicationId,
                primaryRequest);
        Assert.Equal(ApplicationInstanceStartOutcome.Primary, primaryResult.Outcome);
        Assert.False(primaryResult.ShouldExit);
        SingleApplicationInstanceCoordinator primary = primaryResult.PrimaryInstance!;
        Assert.NotNull(primary);
        await using (primary)
        {
            ApplicationStartupRequest forwarded = new(
                Path.GetTempPath(),
                ["project.midora", "", "--名称=弦乐🎻"]);
            ApplicationInstanceStartResult secondary =
                await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                    applicationId,
                    forwarded);

            Assert.Equal(ApplicationInstanceStartOutcome.Forwarded, secondary.Outcome);
            Assert.True(secondary.ShouldExit);
            Assert.Null(secondary.PrimaryInstance);
            ApplicationStartupRequest received = await primary.ReceiveAsync();
            Assert.Equal(forwarded.WorkingDirectory, received.WorkingDirectory);
            Assert.Equal(forwarded.Arguments, received.Arguments);
        }
    }

    [Fact]
    public async Task ConcurrentLaunchRaceProducesExactlyOnePrimary()
    {
        string applicationId = NewApplicationId();
        Task<ApplicationInstanceStartResult>[] starts = Enumerable.Range(0, 8)
            .Select(index => Task.Run(() =>
                SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                    applicationId,
                    Request(index.ToString()))))
            .ToArray();

        ApplicationInstanceStartResult[] results = await Task.WhenAll(starts);
        ApplicationInstanceStartResult primaryResult = Assert.Single(
            results,
            value => value.Outcome == ApplicationInstanceStartOutcome.Primary);
        Assert.Equal(
            7,
            results.Count(value => value.Outcome == ApplicationInstanceStartOutcome.Forwarded));
        await using SingleApplicationInstanceCoordinator primary = primaryResult.PrimaryInstance!;

        List<string> received = [];
        for (int i = 0; i < 7; i++)
        {
            received.Add((await primary.ReceiveAsync()).Arguments[0]);
        }
        Assert.Equal(7, received.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task CrossProcessForwardingUsesOperatingSystemNamespace()
    {
        if (Environment.GetEnvironmentVariable(ChildModeEnvironmentVariable) == "1")
        {
            await RunCrossProcessChildAsync();
            return;
        }

        string repositoryRoot = FindRepositoryRoot();
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"midora-instance-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string readyPath = Path.Combine(temporaryDirectory, "ready");
        string receivedPath = Path.Combine(temporaryDirectory, "received");
        string applicationId = NewApplicationId();
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Reuse the assembly under test rather than inferring a Configuration
        // or output directory from its path. This also supports isolated OutDir
        // regression runs without accidentally executing a stale child binary.
        startInfo.ArgumentList.Add("vstest");
        startInfo.ArgumentList.Add(typeof(SingleApplicationInstanceCoordinatorTests).Assembly.Location);
        startInfo.ArgumentList.Add(
            "--TestCaseFilter:FullyQualifiedName=Midora.Application.Tests.SingleApplicationInstanceCoordinatorTests.CrossProcessForwardingUsesOperatingSystemNamespace");
        startInfo.Environment[ChildModeEnvironmentVariable] = "1";
        startInfo.Environment[ChildApplicationIdEnvironmentVariable] = applicationId;
        startInfo.Environment[ChildReadyPathEnvironmentVariable] = readyPath;
        startInfo.Environment[ChildReceivedPathEnvironmentVariable] = receivedPath;

        using Process child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the instance test child process.");
        Task<string> standardOutput = child.StandardOutput.ReadToEndAsync();
        Task<string> standardError = child.StandardError.ReadToEndAsync();
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            await WaitForFileAsync(readyPath, timeout.Token);
            ApplicationInstanceStartResult forwarded =
                await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                    applicationId,
                    Request("跨进程🎼"),
                    cancellationToken: timeout.Token);
            Assert.Equal(ApplicationInstanceStartOutcome.Forwarded, forwarded.Outcome);

            await child.WaitForExitAsync(timeout.Token);
            string output = await standardOutput;
            string error = await standardError;
            Assert.True(
                child.ExitCode == 0,
                $"Child instance host failed with exit code {child.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
            Assert.Equal("跨进程🎼", await File.ReadAllTextAsync(receivedPath, timeout.Token));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SequentialForwardedRequestsPreserveReceiveOrder()
    {
        string applicationId = NewApplicationId();
        ApplicationInstanceStartResult result =
            await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                applicationId,
                Request("primary"));
        await using SingleApplicationInstanceCoordinator primary = result.PrimaryInstance!;

        foreach (string value in new[] { "first", "second", "third" })
        {
            _ = await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                applicationId,
                Request(value));
        }

        Assert.Equal("first", (await primary.ReceiveAsync()).Arguments[0]);
        Assert.Equal("second", (await primary.ReceiveAsync()).Arguments[0]);
        Assert.Equal("third", (await primary.ReceiveAsync()).Arguments[0]);
    }

    [Fact]
    public async Task DisposeCompletesPendingReceiverAndAllowsNewPrimary()
    {
        string applicationId = NewApplicationId();
        ApplicationInstanceStartResult first =
            await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                applicationId,
                Request("first"));
        SingleApplicationInstanceCoordinator primary = first.PrimaryInstance!;
        Task<ApplicationStartupRequest> pendingReceive = primary.ReceiveAsync().AsTask();

        await primary.DisposeAsync();

        await Assert.ThrowsAsync<ChannelClosedException>(() => pendingReceive);
        ApplicationInstanceStartResult replacement =
            await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                applicationId,
                Request("replacement"));
        Assert.Equal(ApplicationInstanceStartOutcome.Primary, replacement.Outcome);
        await replacement.PrimaryInstance!.DisposeAsync();
    }

    [Fact]
    public async Task TruncatedClientDoesNotTerminateThePrimaryListener()
    {
        string applicationId = NewApplicationId();
        ApplicationInstanceStartResult result =
            await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                applicationId,
                Request("primary"));
        await using SingleApplicationInstanceCoordinator primary = result.PrimaryInstance!;
        byte[] header = new byte[ApplicationInstanceProtocolV1.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ApplicationInstanceProtocolV1.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(4),
            ApplicationInstanceProtocolV1.Version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 32);

        using (NamedPipeClientStream malformed = Client(primary.PipeName))
        {
            await malformed.ConnectAsync(2_000);
            await malformed.WriteAsync(header);
            await malformed.WriteAsync(new byte[4]);
        }

        ApplicationInstanceStartResult secondary =
            await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                applicationId,
                Request("valid"));
        Assert.Equal(ApplicationInstanceStartOutcome.Forwarded, secondary.Outcome);
        Assert.Equal("valid", (await primary.ReceiveAsync()).Arguments[0]);
        Assert.False(primary.Completion.IsCompleted);
    }

    [Fact]
    public async Task InvalidProtocolIsRejectedWithoutEnqueuingPartialRequest()
    {
        string applicationId = NewApplicationId();
        ApplicationInstanceStartResult result =
            await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                applicationId,
                Request("primary"));
        await using SingleApplicationInstanceCoordinator primary = result.PrimaryInstance!;
        byte[] header = new byte[ApplicationInstanceProtocolV1.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xDEADBEEF);

        using NamedPipeClientStream malformed = Client(primary.PipeName);
        await malformed.ConnectAsync(2_000);
        await malformed.WriteAsync(header);
        byte[] response = new byte[1];
        await malformed.ReadExactlyAsync(response);

        Assert.Equal((byte)ApplicationInstanceProtocolResponse.ProtocolRejected, response[0]);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => primary.ReceiveAsync(cancellation.Token).AsTask());
    }

    [Fact]
    public async Task FullQueueIsRejectedAndRecoversAfterConsumerProgress()
    {
        string applicationId = NewApplicationId();
        ApplicationInstanceStartResult result =
            await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                applicationId,
                Request("primary"));
        await using SingleApplicationInstanceCoordinator primary = result.PrimaryInstance!;
        for (int i = 0; i < SingleApplicationInstanceCoordinator.MaximumPendingRequests; i++)
        {
            ApplicationInstanceStartResult forwarded =
                await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                    applicationId,
                    Request(i.ToString()));
            Assert.Equal(ApplicationInstanceStartOutcome.Forwarded, forwarded.Outcome);
        }

        ApplicationInstanceForwardingException full =
            await Assert.ThrowsAsync<ApplicationInstanceForwardingException>(() =>
                SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                    applicationId,
                    Request("overflow")));
        Assert.Equal(ApplicationInstanceForwardingFailure.QueueFull, full.Failure);

        _ = await primary.ReceiveAsync();
        ApplicationInstanceStartResult afterRead =
            await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                applicationId,
                Request("after-read"));
        Assert.Equal(ApplicationInstanceStartOutcome.Forwarded, afterRead.Outcome);
    }

    [Fact]
    public async Task ExistingMarkerWithoutServerFailsClosedThenCanBeReacquired()
    {
        string applicationId = NewApplicationId();
        string objectSuffix =
            SingleApplicationInstanceCoordinator.CreateObjectSuffix(applicationId);
        using (Mutex staleMarker = new(
            initiallyOwned: false,
            SingleApplicationInstanceCoordinator.CreateMutexName(objectSuffix),
            out bool createdNew))
        {
            Assert.True(createdNew);
            ApplicationInstanceForwardingException unavailable =
                await Assert.ThrowsAsync<ApplicationInstanceForwardingException>(() =>
                    SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                        applicationId,
                        Request("blocked"),
                        TimeSpan.FromMilliseconds(50)));
            Assert.Equal(
                ApplicationInstanceForwardingFailure.PrimaryUnavailable,
                unavailable.Failure);
        }

        ApplicationInstanceStartResult replacement =
            await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                applicationId,
                Request("replacement"));
        Assert.Equal(ApplicationInstanceStartOutcome.Primary, replacement.Outcome);
        await replacement.PrimaryInstance!.DisposeAsync();
    }

    [Fact]
    public void StartupRequestRejectsRelativeWorkingDirectoryAndNullArgument()
    {
        Assert.Throws<ArgumentException>(() =>
            new ApplicationStartupRequest("relative", []));
        Assert.Throws<ArgumentException>(() =>
            new ApplicationStartupRequest(Path.GetTempPath(), [null!]));
    }

    [Fact]
    public void ProtocolRejectsTooManyArgumentsAndOversizedPayload()
    {
        ApplicationStartupRequest tooMany = new(
            Path.GetTempPath(),
            Enumerable.Repeat("x", SingleApplicationInstanceCoordinator.MaximumArguments + 1));
        Assert.Throws<ArgumentException>(() =>
            ApplicationInstanceProtocolV1.Serialize(tooMany));

        ApplicationStartupRequest tooLarge = new(
            Path.GetTempPath(),
            [new string('x', SingleApplicationInstanceCoordinator.MaximumPayloadBytes + 1)]);
        Assert.Throws<ArgumentException>(() =>
            ApplicationInstanceProtocolV1.Serialize(tooLarge));

        ApplicationStartupRequest cumulativelyTooLarge = new(
            Path.GetTempPath(),
            [new string('a', 600_000), new string('b', 600_000)]);
        Assert.Throws<ArgumentException>(() =>
            ApplicationInstanceProtocolV1.Serialize(cumulativelyTooLarge));
    }

    [Fact]
    public async Task ProtocolRejectsInvalidUtf8AndTrailingPayload()
    {
        byte[] invalidUtf8Payload = new byte[9];
        BinaryPrimitives.WriteInt32LittleEndian(invalidUtf8Payload, 1);
        invalidUtf8Payload[4] = 0xFF;
        BinaryPrimitives.WriteInt32LittleEndian(invalidUtf8Payload.AsSpan(5), 0);
        byte[] invalidUtf8Frame = Frame(invalidUtf8Payload);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ApplicationInstanceProtocolV1.ReadAsync(
                new MemoryStream(invalidUtf8Frame),
                CancellationToken.None));

        byte[] validFrame = ApplicationInstanceProtocolV1.Serialize(Request("value"));
        byte[] withTrailingByte = new byte[validFrame.Length + 1];
        validFrame.CopyTo(withTrailingByte, 0);
        BinaryPrimitives.WriteInt32LittleEndian(
            withTrailingByte.AsSpan(8),
            validFrame.Length - ApplicationInstanceProtocolV1.HeaderSize + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ApplicationInstanceProtocolV1.ReadAsync(
                new MemoryStream(withTrailingByte),
                CancellationToken.None));
    }

    [Fact]
    public async Task ProtocolRejectsUnknownHeaderFieldsAndInvalidArgumentCounts()
    {
        byte[] validFrame = ApplicationInstanceProtocolV1.Serialize(Request("value"));
        List<byte[]> invalidHeaders = [];
        byte[] unknownVersion = (byte[])validFrame.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(unknownVersion.AsSpan(4), 2);
        invalidHeaders.Add(unknownVersion);
        byte[] nonZeroFlags = (byte[])validFrame.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(nonZeroFlags.AsSpan(6), 1);
        invalidHeaders.Add(nonZeroFlags);
        byte[] nonZeroReserved = (byte[])validFrame.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(nonZeroReserved.AsSpan(12), 1);
        invalidHeaders.Add(nonZeroReserved);
        byte[] negativePayload = (byte[])validFrame.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(negativePayload.AsSpan(8), -1);
        invalidHeaders.Add(negativePayload);
        byte[] oversizedPayload = (byte[])validFrame.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(
            oversizedPayload.AsSpan(8),
            SingleApplicationInstanceCoordinator.MaximumPayloadBytes + 1);
        invalidHeaders.Add(oversizedPayload);

        foreach (byte[] invalid in invalidHeaders)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ApplicationInstanceProtocolV1.ReadAsync(
                    new MemoryStream(invalid),
                    CancellationToken.None));
        }

        int argumentCountOffset = ApplicationInstanceProtocolV1.HeaderSize
            + sizeof(int)
            + System.Text.Encoding.UTF8.GetByteCount(Path.GetTempPath());
        foreach (int invalidCount in new[]
        {
            -1,
            SingleApplicationInstanceCoordinator.MaximumArguments + 1
        })
        {
            byte[] invalid = (byte[])validFrame.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(
                invalid.AsSpan(argumentCountOffset),
                invalidCount);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ApplicationInstanceProtocolV1.ReadAsync(
                    new MemoryStream(invalid),
                    CancellationToken.None));
        }
    }

    [Fact]
    public async Task ForwardCancellationDoesNotCreateAnotherPrimary()
    {
        string applicationId = NewApplicationId();
        ApplicationInstanceStartResult result =
            await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                applicationId,
                Request("primary"));
        await using SingleApplicationInstanceCoordinator primary = result.PrimaryInstance!;
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                applicationId,
                Request("cancelled"),
                cancellationToken: cancellation.Token));

        ApplicationInstanceStartResult forwarded =
            await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                applicationId,
                Request("still-secondary"));
        Assert.Equal(ApplicationInstanceStartOutcome.Forwarded, forwarded.Outcome);
    }

    private static string NewApplicationId() => $"Midora.Tests.{Guid.NewGuid():N}";

    private static ApplicationStartupRequest Request(string argument) =>
        new(Path.GetTempPath(), [argument]);

    private static NamedPipeClientStream Client(string pipeName) =>
        new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    private static byte[] Frame(byte[] payload)
    {
        byte[] frame = new byte[ApplicationInstanceProtocolV1.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, ApplicationInstanceProtocolV1.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(
            frame.AsSpan(4),
            ApplicationInstanceProtocolV1.Version);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), payload.Length);
        payload.CopyTo(frame, ApplicationInstanceProtocolV1.HeaderSize);
        return frame;
    }

    private static async Task RunCrossProcessChildAsync()
    {
        string applicationId = RequireEnvironment(ChildApplicationIdEnvironmentVariable);
        string readyPath = RequireEnvironment(ChildReadyPathEnvironmentVariable);
        string receivedPath = RequireEnvironment(ChildReceivedPathEnvironmentVariable);
        ApplicationInstanceStartResult result =
            await SingleApplicationInstanceCoordinator.StartOrForwardAsync(
                applicationId,
                Request("child-primary"));
        Assert.Equal(ApplicationInstanceStartOutcome.Primary, result.Outcome);
        await using SingleApplicationInstanceCoordinator primary = result.PrimaryInstance!;
        await File.WriteAllTextAsync(readyPath, "ready");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        ApplicationStartupRequest received = await primary.ReceiveAsync(timeout.Token);
        await File.WriteAllTextAsync(receivedPath, received.Arguments[0], timeout.Token);
    }

    private static async Task WaitForFileAsync(string path, CancellationToken cancellationToken)
    {
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    private static string RequireEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing child-process environment variable {name}.");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null
            && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
        {
            current = current.Parent;
        }
        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Midora repository root.");
    }
}
