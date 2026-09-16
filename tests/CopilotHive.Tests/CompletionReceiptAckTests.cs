using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;

using Google.Protobuf;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace CopilotHive.Tests;

/// <summary>
/// STAGE 1 OF THE DURABLE-RECEIPT ACK PROTOCOL: the ADDITIVE wire contract plus CONSERVATIVE
/// registration negotiation.
/// </summary>
/// <para>
/// Two invariants are asserted here and must stay true for every vector:
/// </para>
/// <list type="number">
///   <item><description>The new fields and message are purely ADDITIVE — no existing tag number or
///     reserved range moved, and bytes written by a legacy sender still parse.</description></item>
///   <item><description>Every registration reply reports ACK DISABLED. A request is recorded as an
///     immutable per-registration fact, but a request is never enablement and support is never
///     inferred from capabilities, model or version.</description></item>
/// </list>
/// <remarks>
/// NOT COVERED HERE (deliberately, because this stage does not implement it): ACK emission, receipt
/// comparison, receipt processing/replay, the exclusive per-instance WorkStream attachment claim,
/// and any worker behaviour change.
/// </remarks>
public sealed class CompletionReceiptAckTests
{
    /// <summary>
    /// An OPAQUE task identity that would change meaning under any parsing, splitting or
    /// normalization — it is echoed verbatim or the contract is broken.
    /// </summary>
    private const string OpaqueTaskId = "  ord/0007:3  \u00a0";

    /// <summary>A second opaque identity, distinct from the task id, carried verbatim.</summary>
    private const string OpaqueWorkerId = "worker-07\t:raw ";

    // ═══════════════════════════════════════════════════════════════════════
    // (1) THE ADDITIVE WIRE CONTRACT
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE NEW TAGS, AND ONLY THE NEW TAGS: field 4 on both registration messages and field 6 on
    /// <see cref="OrchestratorMessage"/>. The pre-existing tags are asserted alongside them, so a
    /// renumbering — not merely a collision — fails here.
    /// </summary>
    [Fact]
    public void WireContract_NewFieldsUseTheirAdditiveTagsAndExistingTagsAreUnchanged()
    {
        Assert.Equal(4, RegisterRequest.RequestCompletionReceiptAckFieldNumber);
        Assert.Equal(4, RegisterResponse.CompletionReceiptAckEnabledFieldNumber);
        Assert.Equal(6, OrchestratorMessage.CompletionReceiptAckFieldNumber);

        // The pre-existing registration/stream tags must not have moved.
        Assert.Equal(
            new[] { 1, 3 },
            new[]
            {
                RegisterRequest.WorkerIdFieldNumber,
                RegisterRequest.CapabilitiesFieldNumber,
            });
        Assert.Equal(
            new[] { 1, 2, 3 },
            new[]
            {
                RegisterResponse.AcceptedFieldNumber,
                RegisterResponse.OrchestratorVersionFieldNumber,
                RegisterResponse.AssignedWorkerIdFieldNumber,
            });
        Assert.Equal(
            new[] { 2, 3, 4, 5 },
            new[]
            {
                OrchestratorMessage.AssignmentFieldNumber,
                OrchestratorMessage.CancelFieldNumber,
                OrchestratorMessage.UpdateAgentsFieldNumber,
                OrchestratorMessage.ToolResponseFieldNumber,
            });

        // The new message's own identity tags.
        Assert.Equal(1, CompletionReceiptAck.TaskIdFieldNumber);
        Assert.Equal(2, CompletionReceiptAck.WorkerIdFieldNumber);
    }

    /// <summary>
    /// A REQUEST IS NOT AN ANSWER. Every request shape round-trips: absent and explicit-false are
    /// indistinguishable on the wire (proto3 scalar default), and explicit-true survives.
    /// </summary>
    [Fact]
    public void RegisterRequest_RequestedAck_RoundTripsForAbsentFalseAndTrue()
    {
        var absent = new RegisterRequest { WorkerId = "w" };
        Assert.False(absent.RequestCompletionReceiptAck);

        var absentDecoded = RegisterRequest.Parser.ParseFrom(absent.ToByteArray());
        Assert.False(absentDecoded.RequestCompletionReceiptAck);

        var falseDecoded = RegisterRequest.Parser.ParseFrom(
            new RegisterRequest { WorkerId = "w", RequestCompletionReceiptAck = false }.ToByteArray());
        Assert.False(falseDecoded.RequestCompletionReceiptAck);

        var trueDecoded = RegisterRequest.Parser.ParseFrom(
            new RegisterRequest { WorkerId = "w", RequestCompletionReceiptAck = true }.ToByteArray());
        Assert.True(trueDecoded.RequestCompletionReceiptAck);
    }

    /// <summary>
    /// The ANSWER defaults to disabled and stays disabled under a round-trip — the only value an
    /// orchestrator without a live ACK may send.
    /// </summary>
    [Fact]
    public void RegisterResponse_EnabledAck_DefaultsToFalseAndRoundTrips()
    {
        var response = new RegisterResponse { Accepted = true, AssignedWorkerId = "w" };
        Assert.False(response.CompletionReceiptAckEnabled);

        var decoded = RegisterResponse.Parser.ParseFrom(response.ToByteArray());
        Assert.False(decoded.CompletionReceiptAckEnabled);
        Assert.True(decoded.Accepted);
        Assert.Equal("w", decoded.AssignedWorkerId);
    }

    /// <summary>
    /// A LEGACY ORCHESTRATOR'S REGISTRATION REPLY STILL PARSES: hand-written wire bytes carrying only
    /// fields 1–3 decode with the acknowledgement DISABLED, which is exactly how an old server's
    /// absent field must be read.
    /// </summary>
    [Fact]
    public void RegisterResponse_LegacyWireBytesDecodeWithAckDisabled()
    {
        byte[] legacyBytes;
        using (var stream = new MemoryStream())
        {
            var output = new CodedOutputStream(stream);
            output.WriteTag(RegisterResponse.AcceptedFieldNumber, WireFormat.WireType.Varint);
            output.WriteBool(true);
            output.WriteTag(
                RegisterResponse.OrchestratorVersionFieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteString("0.0.0-legacy");
            output.WriteTag(
                RegisterResponse.AssignedWorkerIdFieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteString("legacy-worker");
            output.Flush();
            legacyBytes = stream.ToArray();
        }

        var decoded = RegisterResponse.Parser.ParseFrom(legacyBytes);

        Assert.True(decoded.Accepted);
        Assert.Equal("0.0.0-legacy", decoded.OrchestratorVersion);
        Assert.Equal("legacy-worker", decoded.AssignedWorkerId);
        Assert.False(decoded.CompletionReceiptAckEnabled);
    }

    /// <summary>
    /// THE OPAQUE IDENTITY IS PRESERVED EXACTLY: whitespace, tabs and non-breaking spaces in the
    /// task and worker identities survive a binary round-trip byte-for-byte, so nothing normalizes
    /// them on the way through.
    /// </summary>
    [Fact]
    public void CompletionReceiptAck_OpaqueIdentities_RoundTripVerbatim()
    {
        var ack = new CompletionReceiptAck { TaskId = OpaqueTaskId, WorkerId = OpaqueWorkerId };

        var decoded = CompletionReceiptAck.Parser.ParseFrom(ack.ToByteArray());

        Assert.Equal(OpaqueTaskId, decoded.TaskId);
        Assert.Equal(OpaqueWorkerId, decoded.WorkerId);
        Assert.Equal(OpaqueTaskId.Length, decoded.TaskId.Length);
        Assert.Equal(OpaqueWorkerId.Length, decoded.WorkerId.Length);
    }

    /// <summary>
    /// The new oneof member is reachable ON THE STREAM and carries the identities verbatim, while
    /// the pre-existing members keep their own cases — an additive member, not a replacement.
    /// </summary>
    [Fact]
    public void OrchestratorMessage_ReceiptAckMemberIsAdditiveAlongsideExistingMembers()
    {
        var message = new OrchestratorMessage
        {
            CompletionReceiptAck = new CompletionReceiptAck
            {
                TaskId = OpaqueTaskId,
                WorkerId = OpaqueWorkerId,
            },
        };

        var decoded = OrchestratorMessage.Parser.ParseFrom(message.ToByteArray());

        Assert.Equal(OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck, decoded.PayloadCase);
        Assert.Equal(OpaqueTaskId, decoded.CompletionReceiptAck.TaskId);
        Assert.Equal(OpaqueWorkerId, decoded.CompletionReceiptAck.WorkerId);

        // The pre-existing members still select their own cases.
        Assert.Equal(
            OrchestratorMessage.PayloadOneofCase.Assignment,
            OrchestratorMessage.Parser
                .ParseFrom(new OrchestratorMessage
                {
                    Assignment = new TaskAssignment { TaskId = "t" },
                }.ToByteArray())
                .PayloadCase);
        Assert.Equal(
            OrchestratorMessage.PayloadOneofCase.Cancel,
            new OrchestratorMessage { Cancel = new CancelTask { TaskId = "t" } }.PayloadCase);
        Assert.Equal(
            OrchestratorMessage.PayloadOneofCase.UpdateAgents,
            new OrchestratorMessage { UpdateAgents = new UpdateAgents() }.PayloadCase);
        Assert.Equal(
            OrchestratorMessage.PayloadOneofCase.ToolResponse,
            new OrchestratorMessage { ToolResponse = new ToolCallResponse() }.PayloadCase);
    }

    /// <summary>
    /// A LEGACY ORCHESTRATOR'S STREAM BYTES STILL PARSE. The bytes are hand-written at the wire
    /// level — one <c>cancel</c> at field 3, with field 6 never written — so this is a genuine
    /// old-sender payload, and re-parsing it yields no receipt-acknowledgement payload at all.
    /// </summary>
    [Fact]
    public void OrchestratorMessage_LegacyWireBytesDecodeWithNoReceiptAck()
    {
        var cancelTask = new CancelTask { TaskId = "t", Reason = "r" };

        byte[] legacyBytes;
        using (var stream = new MemoryStream())
        {
            var output = new CodedOutputStream(stream);
            output.WriteTag(OrchestratorMessage.CancelFieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteMessage(cancelTask);
            output.Flush();
            legacyBytes = stream.ToArray();
        }

        var decoded = OrchestratorMessage.Parser.ParseFrom(legacyBytes);

        Assert.Equal(OrchestratorMessage.PayloadOneofCase.Cancel, decoded.PayloadCase);
        Assert.Null(decoded.CompletionReceiptAck);
        Assert.Equal("t", decoded.Cancel.TaskId);

        // Re-encoding writes no new tag, and an empty message stays payload-free.
        Assert.Equal(legacyBytes.Length, decoded.ToByteArray().Length);
        Assert.Equal(OrchestratorMessage.PayloadOneofCase.None, new OrchestratorMessage().PayloadCase);
    }

    /// <summary>
    /// LEGACY REGISTRATION BYTES STILL PARSE. The bytes below are hand-written at the wire level —
    /// field 1 (worker id) and field 3 (one capability), with field 4 never written — so this is a
    /// genuine old-sender payload rather than a new object with the field left unset.
    /// </summary>
    [Fact]
    public void RegisterRequest_LegacyWireBytes_ParseAndAreUnchangedByReEncoding()
    {
        byte[] legacyBytes;
        using (var stream = new MemoryStream())
        {
            var output = new CodedOutputStream(stream);
            output.WriteTag(RegisterRequest.WorkerIdFieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteString("legacy-worker");
            output.WriteTag(RegisterRequest.CapabilitiesFieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteString("dotnet");
            output.Flush();
            legacyBytes = stream.ToArray();
        }

        var parsed = RegisterRequest.Parser.ParseFrom(legacyBytes);

        Assert.Equal("legacy-worker", parsed.WorkerId);
        Assert.Equal(["dotnet"], parsed.Capabilities);
        Assert.False(parsed.RequestCompletionReceiptAck);

        // Re-encoding writes no new tag: the legacy payload round-trips to the same size and still
        // decodes with no request at all.
        var reEncoded = parsed.ToByteArray();
        Assert.Equal(legacyBytes.Length, reEncoded.Length);

        var reDecoded = RegisterRequest.Parser.ParseFrom(reEncoded);
        Assert.Equal("legacy-worker", reDecoded.WorkerId);
        Assert.Equal(["dotnet"], reDecoded.Capabilities);
        Assert.False(reDecoded.RequestCompletionReceiptAck);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) THE REGISTRATION NEGOTIATION
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FULL REQUEST MATRIX: request absent / explicit false / explicit true. Each registered
    /// instance retains EXACTLY the requested fact — and EVERY reply reports ACK disabled, because a
    /// request is not enablement.
    /// </summary>
    /// <param name="shape">0 = absent, 1 = explicit false, 2 = explicit true.</param>
    /// <param name="expectedFact">The requested fact the registered instance must carry.</param>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task Register_RecordsRequestedFlagAndAlwaysReportsAckDisabled(
        int shape, bool expectedFact)
    {
        var (service, pool) = CreateService();
        var request = new RegisterRequest { WorkerId = "w-ack" };
        switch (shape)
        {
            case 0:
                // Request ABSENT: the field is never touched, so the wire carries nothing.
                Assert.False(request.RequestCompletionReceiptAck);
                break;
            case 1:
                request.RequestCompletionReceiptAck = false;
                break;
            case 2:
                request.RequestCompletionReceiptAck = true;
                break;
            default:
                throw new InvalidOperationException($"unknown request shape '{shape}'");
        }

        var response = await service.Register(request, MockContext());

        Assert.True(response.Accepted);
        Assert.False(
            response.CompletionReceiptAckEnabled,
            "stage 1 must never advertise an ACK it cannot deliver");

        var registered = pool.GetWorker("w-ack");
        Assert.NotNull(registered);
        Assert.Equal(expectedFact, registered!.RequestCompletionReceiptAck);
    }

    /// <summary>
    /// A LEGACY WORKER'S REQUEST IS ABSENT, so the registered fact is false — nothing is inferred
    /// from the capabilities it does advertise.
    /// </summary>
    [Fact]
    public async Task Register_LegacyRequestAbsent_RecordsNoRequestDespiteCapabilities()
    {
        var (service, pool) = CreateService();
        var request = new RegisterRequest { WorkerId = "w-legacy" };
        request.Capabilities.AddRange(["dotnet", "python", "nodejs"]);
        Assert.False(request.RequestCompletionReceiptAck);

        var response = await service.Register(request, MockContext());

        Assert.True(response.Accepted);
        Assert.False(response.CompletionReceiptAckEnabled);

        var registered = pool.GetWorker("w-legacy");
        Assert.NotNull(registered);
        Assert.False(registered!.RequestCompletionReceiptAck);
        Assert.Equal(["dotnet", "python", "nodejs"], registered.Capabilities);
    }

    /// <summary>
    /// DUPLICATE REGISTRATION STAYS REJECTED with no new enabled state: the reply is not accepted,
    /// it reports ACK disabled, and the ORIGINAL instance — including the fact it registered with —
    /// is left completely untouched by the second request.
    /// </summary>
    [Fact]
    public async Task Register_Duplicate_IsRejectedAndLeavesTheOriginalRegistrationIntact()
    {
        var (service, pool) = CreateService();

        var first = await service.Register(
            new RegisterRequest
            {
                WorkerId = "w-dup",
                RequestCompletionReceiptAck = true,
            },
            MockContext());
        Assert.True(first.Accepted);
        Assert.False(first.CompletionReceiptAckEnabled);

        var original = pool.GetWorker("w-dup");
        Assert.NotNull(original);
        Assert.True(original!.RequestCompletionReceiptAck);

        // The duplicate asks for something DIFFERENT; it must change nothing.
        var second = await service.Register(
            new RegisterRequest { WorkerId = "w-dup", RequestCompletionReceiptAck = false },
            MockContext());

        Assert.False(second.Accepted);
        Assert.False(second.CompletionReceiptAckEnabled);

        var afterDuplicate = pool.GetWorker("w-dup");
        Assert.Same(original, afterDuplicate);
        Assert.True(afterDuplicate!.RequestCompletionReceiptAck);
        Assert.Equal(1, pool.ConnectedWorkerCount);
    }

    /// <summary>
    /// A BLANK WORKER ID IS REASSIGNED, and the reassigned instance still carries the requested
    /// fact. The reply reports ACK disabled exactly as before.
    /// </summary>
    [Fact]
    public async Task Register_BlankWorkerId_ReassignedInstanceStillCarriesTheRequestedFact()
    {
        var (service, pool) = CreateService();

        var response = await service.Register(
            new RegisterRequest { WorkerId = "   ", RequestCompletionReceiptAck = true },
            MockContext());

        Assert.True(response.Accepted);
        Assert.False(response.CompletionReceiptAckEnabled);

        var registered = pool.GetWorker(response.AssignedWorkerId);
        Assert.NotNull(registered);
        Assert.True(registered!.RequestCompletionReceiptAck);
    }

    /// <summary>
    /// THE HAND-WRITTEN LEGACY BYTES REACH THE SERVICE UNCHANGED: decoded straight into the
    /// registration RPC, they register a worker that requested nothing and still receive a
    /// DISABLED reply — the old-worker path end to end.
    /// </summary>
    [Fact]
    public async Task Register_LegacyWireBytes_RegisterWorkerWithNoRequestAndDisabledReply()
    {
        byte[] legacyBytes;
        using (var stream = new MemoryStream())
        {
            var output = new CodedOutputStream(stream);
            output.WriteTag(RegisterRequest.WorkerIdFieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteString("w-legacy-wire");
            output.WriteTag(RegisterRequest.CapabilitiesFieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteString("dotnet");
            output.Flush();
            legacyBytes = stream.ToArray();
        }

        var (service, pool) = CreateService();

        var response = await service.Register(
            RegisterRequest.Parser.ParseFrom(legacyBytes), MockContext());

        Assert.True(response.Accepted);
        Assert.False(response.CompletionReceiptAckEnabled);

        var registered = pool.GetWorker("w-legacy-wire");
        Assert.NotNull(registered);
        Assert.False(registered!.RequestCompletionReceiptAck);
        Assert.Equal(["dotnet"], registered.Capabilities);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) REGISTRATION AND THE EXCLUSIVE ATTACHMENT CLAIM
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A REGISTERED INSTANCE IS ATTACHABLE AND CARRIES ITS OWN REQUESTED FACT. The claim is a
    /// per-instance stream-ownership fact: it grants no re-registration authorization, and
    /// registering while another instance holds the same id (after the holder is removed) yields a
    /// NEW instance that is separately eligible — with its own request flag from THAT registration.
    /// </summary>
    [Fact]
    public async Task Register_AfterReRegistration_YieldsANewEligibleInstanceWithItsOwnRequestFlag()
    {
        var (service, pool) = CreateService();

        var first = await service.Register(
            new RegisterRequest { WorkerId = "w-claim", RequestCompletionReceiptAck = true },
            MockContext());
        Assert.True(first.Accepted);
        Assert.False(first.CompletionReceiptAckEnabled);

        var original = pool.GetWorker("w-claim");
        Assert.NotNull(original);
        Assert.True(original!.RequestCompletionReceiptAck);
        Assert.True(original.TryAttachWorkStream());

        // A duplicate is still rejected: the attached instance is untouched by the attempt.
        var duplicate = await service.Register(
            new RegisterRequest { WorkerId = "w-claim", RequestCompletionReceiptAck = false },
            MockContext());
        Assert.False(duplicate.Accepted);
        Assert.False(duplicate.CompletionReceiptAckEnabled);
        Assert.Same(original, pool.GetWorker("w-claim"));
        Assert.True(original.IsWorkStreamAttached);

        // Re-registration after removal is a DIFFERENT object, with a FRESH claim and its OWN
        // requested fact from the registration that created it.
        Assert.True(pool.RemoveWorker(original));

        var second = await service.Register(
            new RegisterRequest { WorkerId = "w-claim", RequestCompletionReceiptAck = false },
            MockContext());
        Assert.True(second.Accepted);
        Assert.False(second.CompletionReceiptAckEnabled);

        var replacement = pool.GetWorker("w-claim");
        Assert.NotNull(replacement);
        Assert.NotSame(original, replacement);
        Assert.False(replacement!.RequestCompletionReceiptAck);
        Assert.False(replacement.IsWorkStreamAttached);

        // The stale instance's claim never transfers; the new instance claims its own.
        Assert.False(original.TryAttachWorkStream());
        Assert.True(replacement.TryAttachWorkStream());
        Assert.Equal(1, pool.ConnectedWorkerCount);
    }

    /// <summary>
    /// ATTACHING DOES NOT CHANGE THE NEGOTIATION ANSWER: a claimed instance still registers as
    /// ACK-disabled, so the attachment claim never becomes an enablement signal.
    /// </summary>
    [Fact]
    public async Task Register_AttachedInstance_StillReportsAckDisabled()
    {
        var (service, pool) = CreateService();

        var response = await service.Register(
            new RegisterRequest { WorkerId = "w-attached", RequestCompletionReceiptAck = true },
            MockContext());

        Assert.True(response.Accepted);
        Assert.False(response.CompletionReceiptAckEnabled);

        var registered = pool.GetWorker("w-attached");
        Assert.NotNull(registered);
        Assert.True(registered!.TryAttachWorkStream());

        // Even after the claim, a repeat registration for the SAME id is still rejected (the
        // instance is registered) and still reports ACK disabled.
        var repeat = await service.Register(
            new RegisterRequest { WorkerId = "w-attached", RequestCompletionReceiptAck = true },
            MockContext());
        Assert.False(repeat.Accepted);
        Assert.False(repeat.CompletionReceiptAckEnabled);
        Assert.Same(registered, pool.GetWorker("w-attached"));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static ServerCallContext MockContext() => new Mock<ServerCallContext>().Object;

    private static (HiveOrchestratorService Service, WorkerPool Pool) CreateService()
    {
        var pool = new WorkerPool();
        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();
        var completionNotifier = new TaskCompletionNotifier();
        var goalManager = new GoalManager();
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(pool),
            completionNotifier,
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        var service = new HiveOrchestratorService(
            pool,
            taskQueue,
            pipelineManager,
            completionNotifier,
            dispatcher,
            NullLogger<HiveOrchestratorService>.Instance);

        return (service, pool);
    }
}
