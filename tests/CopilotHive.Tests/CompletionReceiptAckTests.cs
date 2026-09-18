using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;

using Google.Protobuf;

using Grpc.Core;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

using Moq;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;
using GrpcTaskComplete = CopilotHive.Shared.Grpc.TaskComplete;

namespace CopilotHive.Tests;

/// <summary>
/// THE DURABLE-RECEIPT ACK PROTOCOL'S WIRE CONTRACT AND ITS CONSERVATIVE REGISTRATION NEGOTIATION.
/// </summary>
/// <para>
/// Two invariants are asserted here and must stay true for every vector:
/// </para>
/// <list type="number">
///   <item><description>The new fields and message are purely ADDITIVE — no existing tag number or
///     reserved range moved, and bytes written by a legacy sender still parse.</description></item>
///   <item><description>The REQUEST and the ANSWER are two SEPARATE immutable per-registration facts,
///     both decided before the pool publishes the instance, and the reply is built from that exact
///     instance. A request is never enablement: support is never inferred from capabilities, model
///     or version.</description></item>
/// </list>
/// <remarks>
/// NOT COVERED HERE (deliberately): the acknowledgement's EMISSION and its transport identity, which
/// are driven end to end over the real <c>WorkStream</c> in
/// <see cref="CompletionTransportOwnershipTests"/>, and the same-stream duplicate re-acknowledgement.
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
    /// THE NEW TAGS, AND ONLY THE NEW TAGS: field 4 on both registration messages, field 5 on
    /// <see cref="RegisterResponse"/> and field 6 on <see cref="OrchestratorMessage"/>. The pre-existing
    /// tags are asserted alongside them, so a renumbering — not merely a collision — fails here.
    /// </summary>
    [Fact]
    public void WireContract_NewFieldsUseTheirAdditiveTagsAndExistingTagsAreUnchanged()
    {
        Assert.Equal(4, RegisterRequest.RequestCompletionReceiptAckFieldNumber);
        Assert.Equal(4, RegisterResponse.CompletionReceiptAckEnabledFieldNumber);
        Assert.Equal(6, OrchestratorMessage.CompletionReceiptAckFieldNumber);

        // THE READINESS ADVERTISEMENT IS ADDITIVE TOO — a NEW tag on RegisterResponse, and it is
        // neither a renumbering of field 4 nor a request-side field.
        Assert.Equal(5, RegisterResponse.CompletionReadyRequiredFieldNumber);
        Assert.DoesNotContain(
            RegisterResponse.CompletionReadyRequiredFieldNumber,
            new[]
            {
                RegisterResponse.AcceptedFieldNumber,
                RegisterResponse.OrchestratorVersionFieldNumber,
                RegisterResponse.AssignedWorkerIdFieldNumber,
                RegisterResponse.CompletionReceiptAckEnabledFieldNumber,
            });

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

        // AN OLD ORCHESTRATOR OMITS THE READINESS ADVERTISEMENT TOO, and the omitted field parses as
        // false — never as an implied requirement this server never stated.
        Assert.False(decoded.CompletionReadyRequired);
    }

    /// <summary>
    /// THE READINESS ADVERTISEMENT DEFAULTS TO FALSE AND ROUND-TRIPS: absent, explicit false and
    /// explicit true are all carried exactly, so the field is a genuine per-registration answer
    /// rather than a constant a receiver has to guess.
    /// </summary>
    [Fact]
    public void RegisterResponse_CompletionReadyRequired_DefaultsToFalseAndRoundTrips()
    {
        var absent = new RegisterResponse { Accepted = true, AssignedWorkerId = "w" };
        Assert.False(absent.CompletionReadyRequired);
        Assert.False(
            RegisterResponse.Parser.ParseFrom(absent.ToByteArray()).CompletionReadyRequired);

        var falseDecoded = RegisterResponse.Parser.ParseFrom(
            new RegisterResponse
            {
                Accepted = true,
                CompletionReceiptAckEnabled = false,
                CompletionReadyRequired = false,
            }.ToByteArray());
        Assert.False(falseDecoded.CompletionReadyRequired);
        Assert.False(falseDecoded.CompletionReceiptAckEnabled);

        var trueDecoded = RegisterResponse.Parser.ParseFrom(
            new RegisterResponse
            {
                Accepted = true,
                CompletionReceiptAckEnabled = true,
                CompletionReadyRequired = true,
            }.ToByteArray());
        Assert.True(trueDecoded.CompletionReadyRequired);
        Assert.True(trueDecoded.CompletionReceiptAckEnabled);

        // THE TWO ANSWERS ARE INDEPENDENT FIELDS: the advertisement is not a re-encoding of the
        // acknowledgement enablement, even though this server derives one from the other.
        Assert.False(
            RegisterResponse.Parser.ParseFrom(
                new RegisterResponse { Accepted = true, CompletionReceiptAckEnabled = true }
                    .ToByteArray())
                .CompletionReadyRequired);
    }

    /// <summary>
    /// THE ACCEPTED REPLY ADVERTISES READINESS EXACTLY WHEN THE REGISTRATION IT REGISTERED IS
    /// ACK-ENABLED, for every negotiation cell: the value comes from the RETURNED instance's own
    /// enablement decision, never from a later lookup and never inferred from the request,
    /// capabilities, model or version.
    /// </summary>
    /// <remarks>
    /// THE TWO ANSWERS ARE ASSERTED TOGETHER, and against the PUBLISHED instance, so a reply that
    /// advertised readiness on its own (a field that lies about what will happen) or that disagreed
    /// with the instance the pool actually holds would fail here.
    /// </remarks>
    /// <param name="requested">Whether the registration explicitly requested the acknowledgement.</param>
    /// <param name="withRecorder">Whether the orchestrator has a completion recorder configured.</param>
    /// <param name="expected">The expected value of BOTH answers for this negotiation cell.</param>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task Register_AcceptedReply_AdvertisesReadinessExactlyForAnEnabledInstance(
        bool requested, bool withRecorder, bool expected)
    {
        var (service, pool) = CreateService(withRecorder: withRecorder);

        var response = await service.Register(
            new RegisterRequest { WorkerId = "w-advertised", RequestCompletionReceiptAck = requested },
            MockContext());

        Assert.True(response.Accepted);
        Assert.Equal(expected, response.CompletionReceiptAckEnabled);
        Assert.Equal(expected, response.CompletionReadyRequired);

        var registered = pool.GetWorker("w-advertised");
        Assert.NotNull(registered);
        Assert.Equal(expected, registered!.CompletionReceiptAckEnabled);
        Assert.Equal(registered.CompletionReceiptAckEnabled, response.CompletionReadyRequired);
    }

    /// <summary>
    /// A REJECTED DUPLICATE REPLY ADVERTISES NOTHING: the readiness advertisement is false, exactly
    /// like the acknowledgement enablement, whatever the duplicate asked for — the instance already
    /// registered is untouched.
    /// </summary>
    [Fact]
    public async Task Register_RejectedDuplicateReply_AdvertisesNoReadinessRequirement()
    {
        var (service, pool) = CreateService(withRecorder: true);

        var first = await service.Register(
            new RegisterRequest { WorkerId = "w-dup-advertised", RequestCompletionReceiptAck = true },
            MockContext());
        Assert.True(first.CompletionReadyRequired);

        var original = pool.GetWorker("w-dup-advertised");
        Assert.NotNull(original);

        var duplicate = await service.Register(
            new RegisterRequest { WorkerId = "w-dup-advertised", RequestCompletionReceiptAck = true },
            MockContext());

        Assert.False(duplicate.Accepted);
        Assert.False(duplicate.CompletionReceiptAckEnabled);
        Assert.False(
            duplicate.CompletionReadyRequired,
            "a rejected reply must never advertise a readiness requirement, whatever the duplicate asked for");

        // The original instance is untouched, so the FIRST reply's advertisement still describes it.
        Assert.Same(original, pool.GetWorker("w-dup-advertised"));
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
    /// THE FULL REQUEST MATRIX against an orchestrator with NO COMPLETION RECORDER: request absent /
    /// explicit false / explicit true. Each registered instance retains EXACTLY the requested fact,
    /// and EVERY reply reports ACK DISABLED — because a request is not enablement and there is
    /// nothing here that could deliver an acknowledgement.
    /// </summary>
    /// <param name="shape">0 = absent, 1 = explicit false, 2 = explicit true.</param>
    /// <param name="expectedFact">The requested fact the registered instance must carry.</param>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task Register_WithoutRecorder_RecordsRequestedFlagAndAlwaysReportsAckDisabled(
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
            "an orchestrator without a completion recorder must never advertise an ACK");

        var registered = pool.GetWorker("w-ack");
        Assert.NotNull(registered);
        Assert.Equal(expectedFact, registered!.RequestCompletionReceiptAck);

        // THE TWO FACTS ARE SEPARATE: the request can be true while the enablement is false, and
        // both are readable on the very instance the pool published.
        Assert.False(registered.CompletionReceiptAckEnabled);
    }

    /// <summary>
    /// THE SAME REQUEST MATRIX AGAINST AN ORCHESTRATOR WITH A COMPLETION RECORDER: only the EXPLICIT
    /// request becomes enablement. An absent or explicit-false request stays disabled, and the reply
    /// is built from the instance the pool actually holds — so the two can never disagree.
    /// </summary>
    /// <param name="shape">0 = absent, 1 = explicit false, 2 = explicit true.</param>
    /// <param name="expectedFact">The requested fact the registered instance must carry.</param>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task Register_WithRecorder_EnablesAckOnlyForAnExplicitRequest(
        int shape, bool expectedFact)
    {
        var (service, pool) = CreateService(withRecorder: true);
        var request = new RegisterRequest { WorkerId = "w-negotiated" };
        switch (shape)
        {
            case 0:
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
        Assert.Equal(expectedFact, response.CompletionReceiptAckEnabled);

        var registered = pool.GetWorker("w-negotiated");
        Assert.NotNull(registered);
        Assert.Equal(expectedFact, registered!.RequestCompletionReceiptAck);

        // THE REPLY AND THE PUBLISHED INSTANCE AGREE — the response was built from THIS instance.
        Assert.Equal(expectedFact, registered.CompletionReceiptAckEnabled);
    }

    /// <summary>
    /// A LEGACY WORKER'S REQUEST IS ABSENT, so the registered fact is false and the enablement is
    /// false too — even WITH a recorder configured, and even though the worker advertises generous
    /// capabilities. Nothing is inferred from capabilities, and a request is never inferred from
    /// them either.
    /// </summary>
    [Fact]
    public async Task Register_LegacyRequestAbsent_RecordsNoRequestDespiteCapabilities()
    {
        var (service, pool) = CreateService(withRecorder: true);
        var request = new RegisterRequest { WorkerId = "w-legacy" };
        request.Capabilities.AddRange(["dotnet", "python", "nodejs"]);
        Assert.False(request.RequestCompletionReceiptAck);

        var response = await service.Register(request, MockContext());

        Assert.True(response.Accepted);
        Assert.False(response.CompletionReceiptAckEnabled);

        var registered = pool.GetWorker("w-legacy");
        Assert.NotNull(registered);
        Assert.False(registered!.RequestCompletionReceiptAck);
        Assert.False(registered.CompletionReceiptAckEnabled);
        Assert.Equal(["dotnet", "python", "nodejs"], registered.Capabilities);
    }

    /// <summary>
    /// A FRESH REGISTRATION AFTER A REMOVAL IS A DIFFERENT INSTANCE WITH ITS OWN NEGOTIATED FACTS,
    /// and the two facts are always independently readable on it.
    /// </summary>
    [Fact]
    public async Task Register_FreshInstanceAfterRemoval_CarriesItsOwnEnabledDecision()
    {
        var (service, pool) = CreateService(withRecorder: true);

        var first = await service.Register(
            new RegisterRequest { WorkerId = "w-fresh", RequestCompletionReceiptAck = true },
            MockContext());
        Assert.True(first.CompletionReceiptAckEnabled);

        var original = pool.GetWorker("w-fresh");
        Assert.NotNull(original);
        Assert.True(original!.CompletionReceiptAckEnabled);
        Assert.True(pool.RemoveWorker(original));

        var second = await service.Register(
            new RegisterRequest { WorkerId = "w-fresh" },
            MockContext());

        Assert.True(second.Accepted);
        Assert.False(second.CompletionReceiptAckEnabled);

        var replacement = pool.GetWorker("w-fresh");
        Assert.NotNull(replacement);
        Assert.NotSame(original, replacement);
        Assert.False(replacement!.RequestCompletionReceiptAck);
        Assert.False(replacement.CompletionReceiptAckEnabled);
    }

    /// <summary>
    /// DUPLICATE REGISTRATION STAYS REJECTED with no new enabled state: the reply is not accepted,
    /// it reports ACK disabled, and the ORIGINAL instance — including the fact it registered with —
    /// is left completely untouched by the second request.
    /// </summary>
    [Fact]
    public async Task Register_Duplicate_IsRejectedAndLeavesTheOriginalRegistrationIntact()
    {
        var (service, pool) = CreateService(withRecorder: true);

        var first = await service.Register(
            new RegisterRequest
            {
                WorkerId = "w-dup",
                RequestCompletionReceiptAck = true,
            },
            MockContext());
        Assert.True(first.Accepted);
        Assert.True(first.CompletionReceiptAckEnabled);

        var original = pool.GetWorker("w-dup");
        Assert.NotNull(original);
        Assert.True(original!.RequestCompletionReceiptAck);
        Assert.True(original.CompletionReceiptAckEnabled);

        // The duplicate asks for something DIFFERENT; it must change nothing and advertise nothing.
        var second = await service.Register(
            new RegisterRequest { WorkerId = "w-dup", RequestCompletionReceiptAck = false },
            MockContext());

        Assert.False(second.Accepted);
        Assert.False(
            second.CompletionReceiptAckEnabled,
            "a rejected reply must never advertise an ACK, whatever the duplicate asked for");

        var afterDuplicate = pool.GetWorker("w-dup");
        Assert.Same(original, afterDuplicate);
        Assert.True(afterDuplicate!.RequestCompletionReceiptAck);
        Assert.True(afterDuplicate.CompletionReceiptAckEnabled);
        Assert.Equal(1, pool.ConnectedWorkerCount);
    }

    /// <summary>
    /// A BLANK WORKER ID IS REASSIGNED, and the reassigned instance still carries BOTH negotiated
    /// facts from THAT registration.
    /// </summary>
    [Fact]
    public async Task Register_BlankWorkerId_ReassignedInstanceStillCarriesTheRequestedFact()
    {
        var (service, pool) = CreateService(withRecorder: true);

        var response = await service.Register(
            new RegisterRequest { WorkerId = "   ", RequestCompletionReceiptAck = true },
            MockContext());

        Assert.True(response.Accepted);
        Assert.True(response.CompletionReceiptAckEnabled);

        var registered = pool.GetWorker(response.AssignedWorkerId);
        Assert.NotNull(registered);
        Assert.True(registered!.RequestCompletionReceiptAck);
        Assert.True(registered.CompletionReceiptAckEnabled);
    }

    /// <summary>
    /// THE HAND-WRITTEN LEGACY BYTES REACH THE SERVICE UNCHANGED: decoded straight into the
    /// registration RPC, they register a worker that requested nothing and still receive a
    /// DISABLED reply — the old-worker path end to end, even with a recorder configured.
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

        var (service, pool) = CreateService(withRecorder: true);

        var response = await service.Register(
            RegisterRequest.Parser.ParseFrom(legacyBytes), MockContext());

        Assert.True(response.Accepted);
        Assert.False(response.CompletionReceiptAckEnabled);

        var registered = pool.GetWorker("w-legacy-wire");
        Assert.NotNull(registered);
        Assert.False(registered!.RequestCompletionReceiptAck);
        Assert.False(registered.CompletionReceiptAckEnabled);
        Assert.Equal(["dotnet"], registered.Capabilities);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) REGISTRATION AND THE EXCLUSIVE ATTACHMENT CLAIM
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A REGISTERED INSTANCE IS ATTACHABLE AND CARRIES ITS OWN NEGOTIATED FACTS. The claim is a
    /// per-instance stream-ownership fact: it grants no re-registration authorization, and
    /// registering while another instance holds the same id (after the holder is removed) yields a
    /// NEW instance that is separately eligible — with ITS OWN facts from THAT registration.
    /// </summary>
    [Fact]
    public async Task Register_AfterReRegistration_YieldsANewEligibleInstanceWithItsOwnRequestFlag()
    {
        var (service, pool) = CreateService(withRecorder: true);

        var first = await service.Register(
            new RegisterRequest { WorkerId = "w-claim", RequestCompletionReceiptAck = true },
            MockContext());
        Assert.True(first.Accepted);
        Assert.True(first.CompletionReceiptAckEnabled);

        var original = pool.GetWorker("w-claim");
        Assert.NotNull(original);
        Assert.True(original!.RequestCompletionReceiptAck);
        Assert.True(original.CompletionReceiptAckEnabled);
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
        // negotiated facts from the registration that created it.
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
        Assert.False(replacement.CompletionReceiptAckEnabled);
        Assert.False(replacement.IsWorkStreamAttached);

        // The stale instance's claim never transfers; the new instance claims its own.
        Assert.False(original.TryAttachWorkStream());
        Assert.True(replacement.TryAttachWorkStream());
        Assert.Equal(1, pool.ConnectedWorkerCount);
    }

    /// <summary>
    /// ATTACHING DOES NOT CHANGE THE NEGOTIATION ANSWER: the attachment claim is stream ownership
    /// only, so an enabled instance stays enabled, and a later duplicate for the same id is still
    /// rejected and still reports DISABLED.
    /// </summary>
    [Fact]
    public async Task Register_AttachedInstance_KeepsItsNegotiatedAnswerAndRejectsADuplicate()
    {
        var (service, pool) = CreateService(withRecorder: true);

        var response = await service.Register(
            new RegisterRequest { WorkerId = "w-attached", RequestCompletionReceiptAck = true },
            MockContext());

        Assert.True(response.Accepted);
        Assert.True(response.CompletionReceiptAckEnabled);

        var registered = pool.GetWorker("w-attached");
        Assert.NotNull(registered);
        Assert.True(registered!.TryAttachWorkStream());

        // The claim is stream ownership only — it neither enables nor disables anything.
        Assert.True(registered.CompletionReceiptAckEnabled);

        // Even after the claim, a repeat registration for the SAME id is still rejected (the
        // instance is registered) and still reports ACK disabled.
        var repeat = await service.Register(
            new RegisterRequest { WorkerId = "w-attached", RequestCompletionReceiptAck = true },
            MockContext());
        Assert.False(repeat.Accepted);
        Assert.False(repeat.CompletionReceiptAckEnabled);
        Assert.Same(registered, pool.GetWorker("w-attached"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (4) THE SAME-STREAM DUPLICATE RE-ACKNOWLEDGEMENT
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE MATCHING LATEST DUPLICATE IS RE-ACKNOWLEDGED WITHOUT PROCESSING ANYTHING AGAIN: after one
    /// ordinary enabled completion, a second delivery of that same task — with the successor busy on
    /// a DIFFERENT task — is answered from the retained evidence through the confirming reader's own
    /// forwarding chain, and nothing is released, removed, recorded, dashboard-notified or
    /// completion-notified.
    /// </summary>
    /// <remarks>
    /// THE COUNT IS CUMULATIVE AND THAT IS THE POINT. The ordinary completion already published ONE
    /// acknowledgement for this task, so a count of TWO is the re-acknowledgement; a regression that
    /// published nothing would leave it at one, and one that processed the duplicate again would add a
    /// record, a release or a notification. Everything the duplicate could have changed is asserted
    /// unchanged, including the successor's own ownership AND its activity timestamps.
    /// </remarks>
    [Fact]
    public async Task Duplicate_LatestEligibleMatchingEvidence_IsReAcknowledgedWithoutProcessingAgain()
    {
        const string taskId = "task-dup-match";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            await h.CompleteOrdinaryAsync(taskId);
            Assert.Equal(1, h.AcknowledgedCount(taskId));

            // THE ORDINARY PATH DID NOT READ THE RETAINED EVIDENCE — and a successor is now busy on a
            // DIFFERENT task, which is the ordinary shape a duplicate arrives in.
            Assert.Equal(0, h.Recorder.ConfirmCalls);
            h.AssignSuccessor("task-dup-successor", "successor-model");
            var successorBefore = h.SuccessorSnapshot();
            h.ResetObservations();

            await h.DuplicateAndAwaitReAckAsync(taskId, "assigned-model");

            // ── THE ONLY THING THE DUPLICATE DID ─────────────────────────────────────────────
            Assert.Equal(1, h.Recorder.ConfirmCalls);

            // NO NEW RECORD: the counters were zeroed after the setup, so a zero here means the
            // duplicate re-recorded nothing.
            Assert.Equal(0, h.Recorder.RecordCalls);
            Assert.Equal(2, h.AcknowledgedCount(taskId));
            Assert.Contains(
                h.ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.DuplicateReAcknowledged, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal)
                     && m.Contains(h.Worker.Id, StringComparison.Ordinal));

            // ── …AND EVERYTHING IT DID NOT DO ────────────────────────────────────────────────
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.TasksEnqueued);
            Assert.Equal(successorBefore, h.SuccessorSnapshot());
            Assert.NotNull(h.Queue.GetActiveTask("task-dup-successor"));
            Assert.Null(h.Queue.GetActiveTask(taskId));

            // THE STREAM'S LATEST ELIGIBILITY IS UNCHANGED, proven through the protocol it
            // authorizes: yet another identical duplicate on THIS SAME live stream is still
            // re-acknowledged, which only the retained latest id can permit.
            // (the probe's own Progress barrier legitimately refreshes activity, so the successor
            // fingerprint above is deliberately NOT re-compared after it — its ownership is.)
            await h.AssertLatestEligibleStillReAcknowledgedAsync(taskId, expectedAcknowledgementsAfter: 3);
            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-dup-successor", h.Worker.CurrentTaskId);
        });
    }
    /// <summary>
    /// A DUPLICATE WHOSE CONFIRMATION SAYS <c>false</c> IS NOT ACKNOWLEDGED. The re-sent completion
    /// carries genuinely different evidence, so the read-only check is all that runs: nothing is
    /// re-recorded in the hope of making a later attempt succeed, no acknowledgement is published, and
    /// the latest slot is neither cleared nor advanced.
    /// </summary>
    [Fact]
    public async Task Duplicate_ChangedEvidence_IsNotAcknowledgedAndLeavesTheSlotIntact()
    {
        const string taskId = "task-dup-changed";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            await h.CompleteOrdinaryAsync(taskId);
            Assert.Equal(1, h.AcknowledgedCount(taskId));
            Assert.Equal(0, h.Recorder.ConfirmCalls);

            h.AssignSuccessor("task-dup-changed-successor", "successor-model");
            var successorBefore = h.SuccessorSnapshot();
            h.ResetObservations();

            await h.DuplicateAndAwaitRefusalAsync(taskId, "different-model", "different-output");

            Assert.Equal(1, h.Recorder.ConfirmCalls);

            // NO NEW RECORD either: a mismatched duplicate is never re-recorded in the hope of making a
            // later attempt succeed.
            Assert.Equal(0, h.Recorder.RecordCalls);

            // NO NEW ACKNOWLEDGEMENT — the ordinary completion's own remains the only one.
            Assert.Equal(1, h.AcknowledgedCount(taskId));

            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.TasksEnqueued);
            Assert.Equal(successorBefore, h.SuccessorSnapshot());
            Assert.NotNull(h.Queue.GetActiveTask("task-dup-changed-successor"));

            // THE STREAM'S LATEST ELIGIBILITY IS UNTOUCHED: never cleared, never advanced. Proven
            // by a following MATCHING duplicate on the same live stream, which is re-acknowledged —
            // impossible if the mismatched attempt had cleared or moved the retained latest id.
            await h.AssertLatestEligibleStillReAcknowledgedAsync(taskId, expectedAcknowledgementsAfter: 2);
            Assert.Equal(0, h.Recorder.RecordCalls);
        });
    }

    /// <summary>
    /// A FAILED DUPLICATE DOES NOT CLEAR THE SLOT, PROVEN BY A FOLLOWING ATTEMPT THAT STILL SUCCEEDS:
    /// after the mismatched delivery above, an IDENTICAL re-send of the same latest task is
    /// re-acknowledged. This is the discriminator for a mutant that cleared the slot on failure.
    /// </summary>
    [Fact]
    public async Task Duplicate_AfterAFailedAttempt_StillReAcknowledgesTheSameLatestTask()
    {
        const string taskId = "task-dup-after-failure";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            await h.CompleteOrdinaryAsync(taskId);
            h.AssignSuccessor("task-dup-after-failure-successor", "successor-model");
            var successorBefore = h.SuccessorSnapshot();
            Assert.Equal(1, h.AcknowledgedCount(taskId));
            h.ResetObservations();

            // ── (1) THE MISMATCHED ATTEMPT REFUSES ───────────────────────────────────────────
            await h.DuplicateAndAwaitRefusalAsync(taskId, "different-model", "different-output");
            Assert.Equal(1, h.Recorder.ConfirmCalls);
            Assert.Equal(0, h.Recorder.RecordCalls);
            Assert.Equal(1, h.AcknowledgedCount(taskId));

            // ── (2) THE IDENTICAL RE-SEND STILL SUCCEEDS ─────────────────────────────────────
            // THIS IS THE ELIGIBILITY PROOF ITSELF: the re-acknowledgement is only reachable while
            // the stream's retained latest id still names this task, so a failed attempt that had
            // cleared it would send this delivery down the ordinary path instead.
            await h.DuplicateAndAwaitReAckAsync(taskId, "assigned-model");
            Assert.Equal(2, h.Recorder.ConfirmCalls);
            Assert.Equal(0, h.Recorder.RecordCalls);
            Assert.Equal(2, h.AcknowledgedCount(taskId));

            // ── (3) THE SUCCESSOR IS STILL UNTOUCHED BY EITHER ATTEMPT ───────────────────────
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.TasksEnqueued);
            Assert.Equal(successorBefore, h.SuccessorSnapshot());
            Assert.NotNull(h.Queue.GetActiveTask("task-dup-after-failure-successor"));
        });
    }

    /// <summary>
    /// THE CONFIRMATION'S OWN REFUSALS ARE HANDLED LOCALLY AND PUBLISH NOTHING: a recorder refusal
    /// (<c>StoreError</c>, carrying the exact caught exception) and an unexpected throw both produce no
    /// acknowledgement and no other effect, and the stream survives to handle the following message.
    /// </summary>
    /// <remarks>
    /// THE TWO CELLS ARE SEPARATE INJECTIONS, one per delivery, so the "no acknowledgement" claim is
    /// made against each refusal shape rather than against a single combined one.
    /// </remarks>
    /// <param name="cell">0 = recorder refusal, 1 = unexpected throw.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Duplicate_ConfirmationReadFailure_IsHandledLocally(int cell)
    {
        const string taskId = "task-dup-read-failure";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            await h.CompleteOrdinaryAsync(taskId);
            h.AssignSuccessor("task-dup-read-failure-successor", "successor-model");
            var successorBefore = h.SuccessorSnapshot();
            h.ResetObservations();

            h.Recorder.ConfirmFault = cell switch
            {
                0 => static () => throw WorkerCompletionRecordingException.StoreError(
                    "the retained receipt read threw", new InvalidOperationException("store sentinel")),
                1 => static () => throw new InvalidOperationException("unexpected confirmation sentinel"),
                _ => throw new InvalidOperationException($"unknown cell '{cell}'"),
            };

            await h.DuplicateAndAwaitRefusalAsync(taskId, "assigned-model", null);

            // THE READ WAS REALLY ATTEMPTED AND REALLY FAILED.
            Assert.Equal(1, h.Recorder.ConfirmCalls);

            // NO ACKNOWLEDGEMENT, NO RE-RECORD, NO RELEASE, NO NOTIFICATION, NO SLOT CHANGE.
            Assert.Equal(1, h.AcknowledgedCount(taskId));
            Assert.Equal(0, h.Recorder.RecordCalls);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.TasksEnqueued);
            Assert.Equal(successorBefore, h.SuccessorSnapshot());
            Assert.False(h.StreamEnded);

            // THE ELIGIBILITY SURVIVED THE READ FAILURE: the injected fault is one-shot, so the
            // next identical duplicate reaches the REAL confirmation and is re-acknowledged — which
            // only the stream's still-retained latest id permits.
            await h.AssertLatestEligibleStillReAcknowledgedAsync(taskId, expectedAcknowledgementsAfter: 2);
        });
    }

    /// <summary>
    /// AN ARBITRARY HISTORICAL RECEIPT GETS NO CONFIRMATION READ: a task that was NEVER released on
    /// this stream is not the latest eligible task, so it takes the ordinary path and is refused by the
    /// validated ownership checks — the retention-agnostic read is never even reached.
    /// </summary>
    [Fact]
    public async Task Duplicate_ArbitraryHistoricalTask_GetsNoConfirmationReadAndNoAck()
    {
        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            // THE LATEST ELIGIBLE SLOT IS REAL AND POPULATED, so the refusal below is genuine
            // discrimination rather than a fixture with no eligible task at all.
            await h.CompleteOrdinaryAsync("task-arb-latest");
            Assert.Equal(1, h.AcknowledgedCount("task-arb-latest"));
            Assert.Equal(0, h.Recorder.ConfirmCalls);

            // The worker is idle now, so the ordinary path refuses on the busy/current-task cell.
            h.ResetObservations();

            await h.CompleteAndAwaitOrdinaryRefusalAsync(
                "task-arb-historical",
                HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask);

            // THE LEDGER IS RE-READ THROUGH A LIVE PUMP: no acknowledgement for the historical name —
            // and still exactly the ordinary one for the latest task.
            await h.AssertNoNewAcknowledgementThroughALivePumpAsync(
                ("task-arb-historical", 0), ("task-arb-latest", 1));
            Assert.Equal(0, h.Recorder.ConfirmCalls);
            Assert.Equal(0, h.AcknowledgedCount("task-arb-historical"));
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);

            // THE REAL LATEST ELIGIBILITY IS STILL THE ORIGINAL ONE: the historical name neither
            // replaced it nor cleared it, proven by the latest task's own duplicate still being
            // re-acknowledged on this same live stream.
            await h.AssertLatestEligibleStillReAcknowledgedAsync(
                "task-arb-latest", expectedAcknowledgementsAfter: 2);
            Assert.Equal(0, h.AcknowledgedCount("task-arb-historical"));
        });
    }

    /// <summary>
    /// AN OLDER-THAN-LATEST TASK GETS NO CONFIRMATION READ EITHER: after a SECOND ordinary completion
    /// advances the slot, a duplicate of the FIRST task is no longer the latest eligible completion, so
    /// it takes the ordinary path and is refused there.
    /// </summary>
    /// <remarks>
    /// IT IS ALSO THE SLOT-ADVANCE PROOF: the slot moved from the first task to the second, and the
    /// first task's own retained evidence — which is genuine and unchanged — no longer authorizes
    /// anything. That is exactly what makes this bounded state rather than a history cache.
    /// </remarks>
    [Fact]
    public async Task Duplicate_OlderThanLatestAfterTheSlotAdvanced_GetsNoConfirmationReadAndNoAck()
    {
        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            await h.CompleteOrdinaryAsync("task-older-first");
            Assert.Equal(1, h.AcknowledgedCount("task-older-first"));

            await h.CompleteOrdinaryAsync("task-older-second");
            Assert.Equal(1, h.AcknowledgedCount("task-older-second"));

            // BOTH receipts really are retained — the refusal is about ELIGIBILITY, not about missing
            // evidence.
            Assert.NotNull(h.Stores.ReceiptStore.Load("task-older-first"));
            Assert.NotNull(h.Stores.ReceiptStore.Load("task-older-second"));
            Assert.Equal(0, h.Recorder.ConfirmCalls);

            h.ResetObservations();

            await h.CompleteAndAwaitOrdinaryRefusalAsync(
                "task-older-first",
                HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask);

            // THE LEDGER IS RE-READ THROUGH A LIVE PUMP: the older task's own ordinary acknowledgement
            // remains its only one, and the second completion's is the only one for it.
            await h.AssertNoNewAcknowledgementThroughALivePumpAsync(
                ("task-older-first", 1), ("task-older-second", 1));
            Assert.Equal(0, h.Recorder.ConfirmCalls);
            Assert.Equal(1, h.AcknowledgedCount("task-older-first"));
            Assert.Equal(1, h.AcknowledgedCount("task-older-second"));

            // THE ADVANCE IS PROVEN FROM BOTH SIDES, LIVE. The OLDER id got no read and no
            // re-acknowledgement above — and the SECOND one, which the advance moved the single
            // holder to, still IS re-acknowledged on this very stream.
            await h.AssertLatestEligibleStillReAcknowledgedAsync(
                "task-older-second", expectedAcknowledgementsAfter: 2);
            Assert.Equal(1, h.AcknowledgedCount("task-older-first"));
        });
    }

    /// <summary>
    /// A DISABLED REGISTRATION GETS NO CONFIRMATION READ AND NO RE-ACKNOWLEDGEMENT, even for a task
    /// whose retained evidence matches exactly: the branch requires the negotiated enablement, so a
    /// legacy registration's runtime is untouched.
    /// </summary>
    [Fact]
    public async Task Duplicate_DisabledRegistration_GetsNoConfirmationReadAndNoAck()
    {
        var h = AckHarness.Create(enabled: false);
        await AckHarness.RunAsync(h, async () =>
        {
            Assert.False(h.Worker.CompletionReceiptAckEnabled);

            await h.CompleteOrdinaryWithoutAckAsync("task-disabled-dup");

            // A DISABLED REGISTRATION CREATES NO ELIGIBILITY AND PUBLISHES NO ACKNOWLEDGEMENT.
            Assert.Equal(0, h.AcknowledgedCount("task-disabled-dup"));
            h.ResetObservations();

            await h.CompleteAndAwaitOrdinaryRefusalAsync(
                "task-disabled-dup",
                HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask);

            // THE LEDGER IS RE-READ THROUGH A LIVE PUMP: the disabled registration published nothing,
            // observed after the channel was provably drained.
            await h.AssertNoNewAcknowledgementThroughALivePumpAsync(("task-disabled-dup", 0));
            Assert.Equal(0, h.Recorder.ConfirmCalls);
            Assert.Equal(0, h.AcknowledgedCount("task-disabled-dup"));
            Assert.Equal(0, h.Recorder.RecordCalls);
        });
    }

    /// <summary>
    /// THE DISABLED REGISTRATION IS REFUSED AT ENTRY EVEN IF ITS ELIGIBILITY HOLDER WERE POPULATED —
    /// which is the ONLY way to isolate the negotiation gate, so a TEST-OWNED holder is SEEDED here
    /// and the seed is stated rather than pretended away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THE SEED IS NECESSARY, HONESTLY. Through production paths a disabled registration can never
    /// acquire a latest-eligible id, because the ordinary completion advances the holder only for an
    /// ENABLED registration. So the two gates are normally redundant, and a mutant that dropped the
    /// negotiation check would still pass every other vector in this file. Seeding a holder makes the
    /// negotiation check the ONLY thing that can refuse this delivery, which is exactly what makes the
    /// vector discriminating.
    /// </para>
    /// <para>
    /// THE SEEDED HOLDER IS EXPLICITLY THE TEST'S OWN, AND IT IS SEPARATE. Production's holder belongs
    /// to one live <c>WorkStream</c> invocation and is unreachable from here by construction, so this
    /// vector constructs its own and hands it to a DIRECT handler call. It is never presented as a
    /// live RPC's local, and it changes nothing about what the disabled registration's own runtime
    /// does — the remaining assertions show the delivery is still refused with no read, no
    /// acknowledgement and no processing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Duplicate_DisabledRegistrationWithASeededHolder_IsStillRefusedAtEntry()
    {
        const string taskId = "task-dup-disabled-seeded";

        var h = AckHarness.Create(enabled: false);
        await AckHarness.RunAsync(h, async () =>
        {
            Assert.False(h.Worker.CompletionReceiptAckEnabled);

            // THE SEED, ON A TEST-OWNED HOLDER: the one thing a disabled registration can never do
            // through production.
            var seededState = new WorkStreamCompletionAckState();
            seededState.AdvanceLatestEligible(taskId);
            Assert.Equal(taskId, seededState.LatestEligibleTaskId);

            h.AssignSuccessor("task-dup-disabled-seeded-successor", "successor-model");
            h.ResetObservations();

            // THE PUMP IS PINNED FIRST, so the probe below observes a live, bound pump rather than
            // timing out on a channel nobody drains yet.
            await h.PinPumpAsync();

            h.InvokeDuplicateDirectly(h.Worker, taskId, "assigned-model", output: null, seededState);

            // THE NEGOTIATION GATE REFUSED: no read at all, and the delivery was treated as an
            // ordinary un-owned completion by the validated ownership checks.
            Assert.Equal(0, h.Recorder.ConfirmCalls);
            Assert.Equal(0, h.AcknowledgedCount(taskId));
            Assert.Equal(0, h.Recorder.RecordCalls);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.TasksEnqueued);
            Assert.DoesNotContain(
                h.ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.DuplicateIgnored, StringComparison.Ordinal));

            // THE LEDGER IS RE-READ THROUGH A LIVE PUMP: the direct call's return proves the handler
            // finished, and the probe proves the pump drained — the seeded holder authorized nothing.
            await h.AssertNoNewAcknowledgementThroughALivePumpAsync((taskId, 0));
            Assert.Equal(taskId, seededState.LatestEligibleTaskId);

            // THE SUCCESSOR IS UNTOUCHED.
            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-dup-disabled-seeded-successor", h.Worker.CurrentTaskId);
            Assert.NotNull(h.Queue.GetActiveTask("task-dup-disabled-seeded-successor"));
        });
    }

    /// <summary>
    /// A FRESH STREAM UNDER THE SAME WORKER ID INHERITS NOTHING: after an eligible completion on the
    /// FIRST stream, a REPLACEMENT registers under the SAME id, opens a REAL new <c>WorkStream</c> on
    /// the SAME service with the SAME retained receipt stores, and delivers the OLD completion — which
    /// gets ZERO confirmation reads, NO acknowledgement and mutates nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE EVIDENCE IS GENUINELY THERE, which is what makes the refusal discriminating. The durable
    /// receipt for the old task is still retained in the SAME stores the new stream's service reads,
    /// and the delivery carries the SAME model — so a re-acknowledgement would succeed if anything at
    /// all carried the predecessor stream's eligibility across. The isolation is asserted by a real
    /// behavioural absence (no read, no ack, nothing released, nothing notified), never by reading a
    /// null property.
    /// </para>
    /// <para>
    /// THE OLD COMPLETION TAKES THE ORDINARY PATH on the fresh stream and is refused by the existing
    /// ownership validation, with the replacement's OWN successor assignment left completely intact.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FreshStream_UnderTheSameWorkerId_InheritsNoEligibilityForTheOldCompletion()
    {
        const string taskId = "task-replacement-dup";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            // ── THE FIRST STREAM'S OWN ELIGIBLE COMPLETION ───────────────────────────────────
            await h.CompleteOrdinaryAsync(taskId);
            Assert.Equal(1, h.AcknowledgedCount(taskId));

            // …and it really IS eligible on that stream: an identical duplicate is re-acknowledged.
            await h.AssertLatestEligibleStillReAcknowledgedAsync(taskId, expectedAcknowledgementsAfter: 2);

            // THE RETAINED EVIDENCE SURVIVES THE REPLACEMENT — same stores, same service.
            Assert.NotNull(h.Stores.ReceiptStore.Load(taskId));
            h.ResetObservations();

            // ── THE REPLACEMENT, under the SAME worker id and the SAME negotiated enablement ──
            var replacement = h.ReplacePinnedInstance();
            Assert.NotSame(h.Worker, replacement);
            Assert.True(replacement.CompletionReceiptAckEnabled);

            // ── A REAL FRESH WorkStream FOR IT, on the SAME service ──────────────────────────
            var freshStream = h.StartStream(replacement.Id);
            await h.BarrierOnAsync(freshStream);
            Assert.True(replacement.IsWorkStreamAttached);

            // The replacement owns its OWN work, so a successor really exists to be disturbed.
            h.AssignTaskTo(replacement, "task-replacement-successor", "successor-model");
            h.ResetObservations();

            // THE BASELINE, taken after the FIRST stream's own legitimate re-acknowledgement above,
            // so the claim below is about what the FRESH stream did and nothing earlier.
            var reAckedBefore =
                h.DiagnosticCount(ProductionLogFragments.DuplicateReAcknowledged, taskId);

            // ── THE OLD COMPLETION, DELIVERED THROUGH THAT REAL FRESH STREAM ─────────────────
            await h.CompleteAndAwaitOrdinaryRefusalOnAsync(
                freshStream,
                replacement,
                taskId,
                HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask);

            // ZERO CONFIRMATION READS: the fresh stream's own holder never named this task, so the
            // read-only branch was never entered at all.
            Assert.Equal(0, h.Recorder.ConfirmCalls);
            Assert.Equal(0, h.Recorder.RecordCalls);
            Assert.Equal(
                reAckedBefore,
                h.DiagnosticCount(ProductionLogFragments.DuplicateReAcknowledged, taskId));

            // NO ACKNOWLEDGEMENT on the fresh stream — observed after ITS pump provably drained.
            await h.AssertNoNewAcknowledgementThroughALivePumpOnAsync(
                freshStream, replacement, (taskId, 0));

            // NO SUCCESSOR MUTATION: the replacement still holds its own assignment, in both
            // authorities, and nothing was released or notified.
            Assert.True(replacement.IsBusy);
            Assert.Equal("task-replacement-successor", replacement.CurrentTaskId);
            Assert.NotNull(h.Queue.GetActiveTask("task-replacement-successor"));
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);
        });
    }

    /// <summary>
    /// TWO DISTINCT LIVE STREAMS ON THE SAME SERVICE RETAIN INDEPENDENT LATEST ELIGIBILITY: each
    /// completes its OWN task and re-acknowledges ONLY that task, while the OTHER stream's task gets
    /// no read and no acknowledgement from it.
    /// </summary>
    /// <remarks>
    /// EACH STREAM IS A REAL <c>WorkStream</c> INVOCATION, so the eligibility exercised is genuinely
    /// per invocation. Both share the service, the queue, the pool and the receipt stores — so the
    /// cross-stream refusals below cannot be explained by missing evidence: the other stream's receipt
    /// is durably retained and would confirm if anything at all were shared.
    /// </remarks>
    [Fact]
    public async Task TwoLiveStreams_RetainIndependentLatestEligibility()
    {
        const string firstTaskId = "task-two-streams-first";
        const string secondTaskId = "task-two-streams-second";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            // ── THE SECOND WORKER AND ITS OWN REAL STREAM ────────────────────────────────────
            var secondWorker = h.RegisterAdditionalWorker("ack-duplicate-worker-2");
            var secondStream = h.StartStream(secondWorker.Id);
            await h.BarrierOnAsync(secondStream);
            Assert.True(secondWorker.IsWorkStreamAttached);

            // ── EACH STREAM COMPLETES ITS OWN TASK ───────────────────────────────────────────
            await h.CompleteOrdinaryAsync(firstTaskId);
            await h.CompleteOrdinaryOnAsync(secondStream, secondWorker, secondTaskId);

            Assert.Equal(1, h.AcknowledgedCount(firstTaskId));
            Assert.Equal(1, AckHarness.AcknowledgedCountOn(secondStream, secondTaskId));

            // BOTH receipts are durably retained in the SHARED stores.
            Assert.NotNull(h.Stores.ReceiptStore.Load(firstTaskId));
            Assert.NotNull(h.Stores.ReceiptStore.Load(secondTaskId));
            h.ResetObservations();

            // ── EACH STREAM RE-ACKNOWLEDGES ONLY ITS OWN LATEST TASK ─────────────────────────
            await h.AssertLatestEligibleStillReAcknowledgedAsync(
                firstTaskId, expectedAcknowledgementsAfter: 2);
            await h.DuplicateAndAwaitReAckOnAsync(secondStream, secondWorker, secondTaskId);
            Assert.Equal(2, AckHarness.AcknowledgedCountOn(secondStream, secondTaskId));

            // ── AND NEITHER CAN ANSWER FOR THE OTHER'S TASK ──────────────────────────────────
            var confirmationsBefore = h.Recorder.ConfirmCalls;

            // The SECOND stream's worker is idle and its holder never named the FIRST task, so that
            // delivery takes the ORDINARY path and is refused there — with no read at all.
            await h.CompleteAndAwaitOrdinaryRefusalOnAsync(
                secondStream,
                secondWorker,
                firstTaskId,
                HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask);

            // …and symmetrically for the FIRST stream against the SECOND stream's task.
            await h.CompleteAndAwaitOrdinaryRefusalAsync(
                secondTaskId,
                HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask);

            Assert.Equal(confirmationsBefore, h.Recorder.ConfirmCalls);
            Assert.Equal(0, h.Recorder.RecordCalls);

            // NOTHING CROSSED, observed after BOTH pumps provably drained.
            await h.AssertNoNewAcknowledgementThroughALivePumpAsync((firstTaskId, 2), (secondTaskId, 0));
            await h.AssertNoNewAcknowledgementThroughALivePumpOnAsync(
                secondStream, secondWorker, (secondTaskId, 2), (firstTaskId, 0));
        });
    }

    /// <summary>
    /// ABA DURING THE CONFIRMATION READ: the pinned instance is replaced while the read is in flight, so
    /// the post-read recheck refuses and NOTHING is acknowledged — the replacement is never answered on
    /// its predecessor's evidence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE MUTATION IS DETERMINISTIC: it runs synchronously inside the confirmation call itself, on the
    /// handler's own thread, immediately after the REAL confirmation returned <c>true</c>. That is the
    /// only way to make the read succeed and the recheck fail, which is exactly the interleaving this
    /// recheck exists for.
    /// </para>
    /// <para>
    /// THE HANDLER IS INVOKED DIRECTLY, for the reason the existing ABA vector gives: replacing the
    /// pinned instance makes the real read loop's OWN per-message pinned-instance guard end the stream
    /// on the next message, so a post-handler barrier could never run. The direct, synchronous call's
    /// RETURNING is itself the barrier, and any escaping exception surfaces here rather than in a
    /// swallowed stream fault.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Duplicate_PinnedInstanceReplacedDuringTheConfirmationRead_IsRefused()
    {
        const string taskId = "task-dup-aba";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            // THE ONE TEST-OWNED HOLDER BOTH DIRECT CALLS SHARE. Production's holder belongs to a
            // live WorkStream invocation; these are DIRECT handler calls, so the ordinary completion
            // and the duplicate that follows it must be given the SAME caller-owned holder or the
            // duplicate would have nothing to be a duplicate OF.
            var directState = new WorkStreamCompletionAckState();

            await h.CompleteOrdinaryDirectAsync(taskId, directState);
            h.AssignSuccessor("task-dup-aba-successor", "successor-model");
            Assert.Equal(1, h.AcknowledgedCount(taskId));
            h.ResetObservations();

            ConnectedWorker? replacement = null;
            h.Recorder.BeforeReturn = () =>
            {
                Assert.True(h.Pool.RemoveWorker(h.Worker));
                replacement = h.RegisterReplacementInstance();

                // THE REPLACEMENT'S OWN STATE-CHANGE NOTIFICATION IS SETUP NOISE, NOT THE DUPLICATE'S
                // EFFECT: registering legitimately raises one, and it happens inside the read purely
                // because that is where this ABA is injected. Clearing the counter HERE — after the
                // registration, before the read returns — leaves exactly what the duplicate itself
                // raised, which is what the assertion below is about.
                h.ResetDashboardNotifications();
            };

            // A DIRECT, SYNCHRONOUS call: it RETURNING is the post-handler barrier.
            h.InvokeDuplicateDirectly(h.Worker, taskId, "assigned-model", output: null, directState);

            // THE READ REALLY SUCCEEDED AND THE RECHECK REALLY REFUSED.
            Assert.Equal(1, h.Recorder.ConfirmCalls);
            Assert.Contains(
                h.ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.DuplicateIgnored, StringComparison.Ordinal)
                     && m.Contains(
                         HiveOrchestratorService.OwnershipRefusalReasons.PinnedInstanceReplaced,
                         StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));

            // NO RE-ACKNOWLEDGEMENT: the ordinary completion's own acknowledgement remains the only one
            // for this task. The direct, synchronous call RETURNING is itself the post-handler barrier
            // here: the read loop ended when the replacement landed (the pinned-instance guard), so a
            // pump probe cannot observe this channel — the enqueue path is proven by the refusal
            // diagnostic's reason instead of by a writer probe.
            Assert.Equal(1, h.AcknowledgedCount(taskId));
            Assert.Equal(0, h.Recorder.RecordCalls);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.TasksEnqueued);

            // THE REPLACEMENT WAS NEVER ANSWERED ON ITS PREDECESSOR'S EVIDENCE: the refusal named
            // the pinned-instance guard above, and the ledger carries no acknowledgement beyond the
            // ordinary completion's own.
            Assert.NotNull(replacement);
            Assert.True(replacement!.CompletionReceiptAckEnabled);
            Assert.DoesNotContain(
                h.ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.DuplicateReAcknowledged, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));

            // THE CALLER'S OWN HOLDER WAS NEITHER ADVANCED NOR CLEARED by the refused duplicate.
            Assert.Equal(taskId, directState.LatestEligibleTaskId);
        });
    }

    /// <summary>
    /// A PRE-EXISTING HELD TASK NEVER ENTERS THE READ-ONLY BRANCH, AND ITS COMPLETION IS HANDLED
    /// ORDINARILY. The latest eligible id is RE-DISPATCHED — active in the queue for this worker and
    /// busy in the pool — so the completion that follows it is a GENUINE completion: it must be
    /// recorded and released, not compared against the old receipt and discarded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS IS THE INITIAL (PRE-READ) GATE'S OWN VECTOR. The proof that the branch was never entered
    /// is the confirmation count: ZERO reads happened, so the delivery cannot have travelled the
    /// read-only path at all. The proof that it was handled ORDINARILY is positive rather than
    /// negative — the recorder ran, the checked release applied, the queue entry was removed and the
    /// real downstream chain was notified.
    /// </para>
    /// <para>
    /// THE ACTIVITY REFRESH IS ASSERTED THROUGH THE REAL READ LOOP, which is the only place the
    /// exemption lives. The worker's activity clock is pushed back before the delivery, so a refresh
    /// is observable; a genuinely held completion MUST get it, or inactivity-based reclamation could
    /// evict a worker that is actively reporting.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Duplicate_PreExistingHeldTask_NeverReadsAndIsHandledOrdinarily()
    {
        const string taskId = "task-dup-still-held";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            await h.CompleteOrdinaryAsync(taskId);
            Assert.Equal(1, h.AcknowledgedCount(taskId));

            // THE ELIGIBILITY REALLY EXISTS FOR THIS TASK on this stream — so the vector below
            // genuinely discriminates the HELD gate rather than an empty holder.
            await h.AssertLatestEligibleStillReAcknowledgedAsync(taskId, expectedAcknowledgementsAfter: 2);

            // THE DUPLICATE-BRANCH DIAGNOSTIC BASELINE, taken AFTER that probe and BEFORE the
            // delivery under test, so the "never entered the branch" claims below are about THIS
            // delivery rather than about the probe that legitimately used the branch.
            var ignoredBefore = h.DiagnosticCount(ProductionLogFragments.DuplicateIgnored, taskId);
            var reAckedBefore =
                h.DiagnosticCount(ProductionLogFragments.DuplicateReAcknowledged, taskId);

            // THE TASK IS RE-DISPATCHED: active in the queue for this worker and busy in the pool —
            // a live assignment again, exactly as a genuine re-dispatch leaves it.
            h.ReactivateLatestTask(taskId);
            h.ResetObservations();

            // THE ACTIVITY CLOCK IS PUSHED BACK, so the refresh the read loop owes a genuinely held
            // completion is observable rather than assumed.
            var staleActivity = h.PushActivityClockBack();

            // THE DELIVERY TRAVELS THE REAL READ LOOP and is ACCEPTED as an ordinary completion. The
            // activity clock is read AT ACCEPTANCE — before any barrier, because the barrier's own
            // Progress message legitimately refreshes it and would mask a withheld refresh.
            var activityAtAcceptance = await h.CompleteHeldTaskOrdinarilyAsync(taskId);

            // ── THE BRANCH WAS NEVER ENTERED BY THIS DELIVERY ───────────────────────────────
            // Not one confirmation read happened since the reset, so it never reached the read-only
            // path — and neither duplicate diagnostic moved past its pre-delivery baseline.
            Assert.Equal(0, h.Recorder.ConfirmCalls);
            Assert.Equal(
                ignoredBefore, h.DiagnosticCount(ProductionLogFragments.DuplicateIgnored, taskId));
            Assert.Equal(
                reAckedBefore,
                h.DiagnosticCount(ProductionLogFragments.DuplicateReAcknowledged, taskId));

            // ── IT WAS HANDLED ORDINARILY: recorded, released, removed and notified ──────────
            Assert.Equal(1, h.Recorder.RecordCalls);
            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Worker.CurrentTaskId);
            Assert.Null(h.Queue.GetActiveTask(taskId));
            Assert.Equal(1, h.TransportNotifications);
            Assert.Equal(1, h.DashboardNotifications);

            // …and the ordinary path published its own acknowledgement, so the completion was not
            // swallowed: the ledger now carries the first completion's ack, the eligibility probe's
            // re-acknowledgement, and this one's.
            Assert.Equal(3, h.AcknowledgedCount(taskId));

            // ── THE ACTIVITY REFRESH WAS NOT WITHHELD ───────────────────────────────────────
            // The read loop's exemption must not apply to a genuinely held completion, or the
            // pinned worker could be reclaimed for inactivity while it is actively reporting. The
            // instant compared here was captured AT ACCEPTANCE, so it reflects the loop's own
            // refresh decision for THIS delivery and nothing later.
            Assert.True(
                activityAtAcceptance > staleActivity,
                "a genuinely held completion must keep its normal activity refresh");
        });
    }

    /// <summary>
    /// EACH HALF OF THE PRE-READ NOT-HELD CONDITION IS INDEPENDENTLY REQUIRED: a latest task that is
    /// active in the queue but not held by the worker, OR held by the worker but absent from the
    /// queue, never reaches confirmation. It falls into the ordinary ownership validation and is
    /// refused there, with its pre-existing one-sided state untouched.
    /// </summary>
    /// <remarks>
    /// THE CELLS ARE REMOVAL-SENSITIVE. In the queue-only cell, deleting the queue half of the entry
    /// gate would allow the real retained receipt to confirm; in the worker-only cell, deleting the
    /// current-task half would do the same. Setting both facts in one vector cannot distinguish those
    /// regressions, which is why these are separate from the full re-dispatch acceptance test above.
    /// </remarks>
    /// <param name="cell">0 = active queue entry only; 1 = worker-current ownership only.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Duplicate_PreReadNotHeldGate_EachAuthorityIndependentlyPreventsConfirmation(int cell)
    {
        const string taskId = "task-dup-one-sided-held";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            await h.CompleteOrdinaryAsync(taskId);
            Assert.Equal(1, h.AcknowledgedCount(taskId));
            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Queue.GetActiveTask(taskId));
            h.ResetObservations();

            string expectedOrdinaryRefusal;
            switch (cell)
            {
                case 0:
                    // PRE-EXISTING QUEUE-ONLY STATE: the old id is active again, but the worker is
                    // still idle. Only the queue half of the duplicate entry gate can reject it.
                    h.Queue.Activate(h.BuildTask(taskId, "assigned-model"), h.Worker.Id);
                    Assert.False(h.Worker.IsBusy);
                    Assert.NotNull(h.Queue.GetActiveTask(taskId));
                    expectedOrdinaryRefusal =
                        HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask;
                    break;

                case 1:
                    // PRE-EXISTING WORKER-ONLY STATE: the worker holds the old id again, but there is
                    // no active queue entry. Only the current-task half can reject duplicate entry.
                    h.Pool.MarkBusy(h.Worker.Id, taskId);
                    h.Worker.CurrentModel = "assigned-model";
                    Assert.True(h.Worker.IsBusy);
                    Assert.Equal(taskId, h.Worker.CurrentTaskId);
                    Assert.Null(h.Queue.GetActiveTask(taskId));
                    expectedOrdinaryRefusal =
                        HiveOrchestratorService.OwnershipRefusalReasons.NoActiveQueueEntry;
                    break;

                default:
                    throw new InvalidOperationException($"unknown one-sided held cell '{cell}'");
            }

            await h.CompleteAndAwaitOrdinaryRefusalAsync(taskId, expectedOrdinaryRefusal);

            // NO CONFIRMATION READ: this is the discriminator for the PRE-read gate. The real stored
            // receipt matches, so entering the duplicate branch would increment this and authorize an
            // acknowledgement unless the tested authority check stopped it.
            Assert.Equal(0, h.Recorder.ConfirmCalls);
            Assert.Equal(0, h.Recorder.RecordCalls);

            // HANDLER BARRIER ABOVE, THEN PUMP OBSERVATION BARRIER HERE: the ordinary completion's
            // acknowledgement remains the only one after the channel is provably drained.
            await h.AssertNoNewAcknowledgementThroughALivePumpAsync((taskId, 1));
            Assert.Equal(1, h.AcknowledgedCount(taskId));

            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.TasksEnqueued);

            // The one-sided pre-existing state was not changed by the refused ordinary path.
            Assert.Equal(cell == 1, h.Worker.IsBusy);
            Assert.Equal(cell == 1 ? taskId : null, h.Worker.CurrentTaskId);
            Assert.Equal(cell == 0, h.Queue.GetActiveTask(taskId) is not null);

            // THE ELIGIBILITY SURVIVED: once the one-sided hold is cleared, an identical duplicate
            // is re-acknowledged again, so the refusal above really was the entry gate and not a
            // lost or cleared latest id.
            await h.AssertLatestEligibleSurvivedAndReAcknowledgesAfterReleaseAsync(
                taskId, expectedAcknowledgementsAfter: 2);
        });
    }

    /// <summary>
    /// THE POST-READ RECHECK'S OWN VECTOR: the task is NOT held when the duplicate arrives — so the
    /// initial gate passes and the confirmation read really happens — and is RE-DISPATCHED
    /// DETERMINISTICALLY DURING that read, so the recheck afterwards refuses and nothing is
    /// acknowledged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THE MUTATION IS INSIDE THE READ. The two gates answer different questions, and only this
    /// interleaving isolates the second one: the entry gate cannot refuse a delivery that was
    /// genuinely unheld on arrival, so a refusal here can ONLY come from the post-read recheck. The
    /// mutation runs synchronously on the handler's own thread inside the confirmation call, so the
    /// interleaving is exact rather than raced.
    /// </para>
    /// <para>
    /// IT RUNS THROUGH THE REAL READ LOOP. Nothing here replaces the pinned instance, so the stream
    /// keeps running and the post-handler barrier proves the handler RETURNED.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Duplicate_TaskReDispatchedDuringTheConfirmationRead_IsRefusedAfterTheRead()
    {
        const string taskId = "task-dup-held-during-read";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            await h.CompleteOrdinaryAsync(taskId);
            Assert.Equal(1, h.AcknowledgedCount(taskId));

            // THE TASK IS GENUINELY UNHELD WHEN THE DUPLICATE ARRIVES: released, and gone from the
            // queue. So the initial gate cannot be what refuses this delivery.
            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Queue.GetActiveTask(taskId));
            h.ResetObservations();

            // THE RE-DISPATCH LANDS INSIDE THE READ, after the real confirmation answered.
            h.Recorder.BeforeReturn = () => h.ReactivateLatestTask(taskId);

            await h.DuplicateAndAwaitRefusalAsync(
                taskId,
                "assigned-model",
                output: null,
                expectedReason: HiveOrchestratorService.OwnershipRefusalReasons.LatestEligibleTaskStillHeld);

            // IT GOT ALL THE WAY TO THE READ — so the refusal is the RECHECK, not the entry gate.
            Assert.Equal(1, h.Recorder.ConfirmCalls);

            // NO ACKNOWLEDGEMENT, and nothing was processed again.
            Assert.Equal(1, h.AcknowledgedCount(taskId));
            await h.AssertNoNewAcknowledgementThroughALivePumpAsync((taskId, 1));
            Assert.Equal(0, h.Recorder.RecordCalls);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.TasksEnqueued);

            // THE RE-DISPATCHED OWNERSHIP IS UNDISTURBED: the duplicate released nothing.
            Assert.True(h.Worker.IsBusy);
            Assert.Equal(taskId, h.Worker.CurrentTaskId);
            Assert.NotNull(h.Queue.GetActiveTask(taskId));

            // THE STREAM'S ELIGIBILITY SURVIVES A FAILED DUPLICATE: once the re-dispatch is undone,
            // an identical duplicate is re-acknowledged again on this same live stream.
            await h.AssertLatestEligibleSurvivedAndReAcknowledgesAfterReleaseAsync(
                taskId, expectedAcknowledgementsAfter: 2);
        });
    }

    /// <summary>
    /// A DUPLICATE NAMING AN UNKNOWN WIRE STATUS IS MALFORMED INPUT AND IS HANDLED LOCALLY: it is
    /// refused at the mapping step, BEFORE the confirmation read and before any acknowledgement.
    /// </summary>
    [Fact]
    public async Task Duplicate_MalformedInput_IsHandledLocallyWithoutAReadOrAnAck()
    {
        const string taskId = "task-dup-malformed";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            await h.CompleteOrdinaryAsync(taskId);
            h.AssignSuccessor("task-dup-malformed-successor", "successor-model");
            var successorBefore = h.SuccessorSnapshot();
            h.ResetObservations();

            // A PRESENT MODEL, so the refusal is provably the MAPPING and not the presence gate.
            await h.DuplicateAndAwaitMappingRefusalAsync(
                taskId, "assigned-model", (CopilotHive.Shared.Grpc.TaskStatus)9999);

            Assert.Equal(0, h.Recorder.ConfirmCalls);
            Assert.Equal(1, h.AcknowledgedCount(taskId));
            Assert.Equal(0, h.Recorder.RecordCalls);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.TasksEnqueued);
            Assert.Equal(successorBefore, h.SuccessorSnapshot());

            // THE ELIGIBILITY SURVIVED THE MALFORMED DELIVERY: a well-formed identical duplicate is
            // still re-acknowledged on this same live stream.
            await h.AssertLatestEligibleStillReAcknowledgedAsync(taskId, expectedAcknowledgementsAfter: 2);
        });
    }

    /// <summary>
    /// A DUPLICATE WITHOUT MODEL PRESENCE IS REFUSED BEFORE THE READ: an enabled registration must
    /// report the model, on the duplicate path exactly as on the ordinary one, so the confirmation is
    /// never even attempted.
    /// </summary>
    [Fact]
    public async Task Duplicate_AbsentModel_IsRefusedBeforeTheRead()
    {
        const string taskId = "task-dup-absent-model";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            await h.CompleteOrdinaryAsync(taskId);
            h.AssignSuccessor("task-dup-absent-model-successor", "successor-model");
            h.ResetObservations();

            await h.DuplicateAndAwaitRefusalAsync(
                taskId,
                model: null,
                output: null,
                expectedReason: HiveOrchestratorService.OwnershipRefusalReasons.ModelPresenceRequired);

            Assert.Equal(0, h.Recorder.ConfirmCalls);
            Assert.Equal(1, h.AcknowledgedCount(taskId));
            Assert.Equal(0, h.Recorder.RecordCalls);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);

            // THE ELIGIBILITY SURVIVED THE PRESENCE REFUSAL: a model-carrying identical duplicate is
            // still re-acknowledged on this same live stream.
            await h.AssertLatestEligibleStillReAcknowledgedAsync(taskId, expectedAcknowledgementsAfter: 2);
        });
    }

    /// <summary>
    /// A LOSING STREAM'S DUPLICATE HAS NO EFFECT: a second stream for the SAME registered instance loses
    /// the exclusive attachment claim on its very first message, so the duplicate it carried is never
    /// handled — no confirmation read, no acknowledgement, and no change to the winner's state or to its
    /// successor.
    /// </summary>
    [Fact]
    public async Task Duplicate_LosingStream_HasNoReadAndNoAck()
    {
        const string taskId = "task-dup-losing-stream";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            await h.CompleteOrdinaryAsync(taskId);
            h.AssignSuccessor("task-dup-losing-stream-successor", "successor-model");
            var successorBefore = h.SuccessorSnapshot();
            h.ResetObservations();

            // THE LOSING STREAM's first message IS the duplicate.
            var loser = await h.StartSecondStreamAndAwaitTerminationAsync(
                h.Worker.Id,
                new WorkerMessage
                {
                    WorkerId = h.Worker.Id,
                    Complete = AckHarness.BuildDuplicate(taskId, "assigned-model", output: null),
                });

            Assert.False(
                loser.Completion!.Faulted,
                $"a losing stream must return normally: {loser.Completion.Fault}");
            Assert.Contains(
                h.ServiceLogger.Messages,
                m => m.Contains(
                         HiveOrchestratorService.OwnershipRefusalReasons.WorkStreamAlreadyAttached,
                         StringComparison.Ordinal)
                     && m.Contains(h.Worker.Id, StringComparison.Ordinal));

            // NO READ, NO ACK, NOTHING ELSE — re-read through the WINNER's live pump, which is the
            // only pump bound to the instance's channel, so the loser could only publish through it.
            Assert.Equal(0, h.Recorder.ConfirmCalls);
            Assert.Equal(1, h.AcknowledgedCount(taskId));
            Assert.Equal(0, h.Recorder.RecordCalls);
            await h.AssertNoNewAcknowledgementThroughALivePumpAsync((taskId, 1));
            Assert.Empty(loser.Writer.Acknowledgements());
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.TasksEnqueued);
            Assert.Equal(successorBefore, h.SuccessorSnapshot());

            // THE WINNER'S OWN ELIGIBILITY IS UNTOUCHED by the loser: the winner's identical
            // duplicate is still re-acknowledged on ITS live stream — and the loser's writer still
            // carries nothing.
            await h.AssertLatestEligibleStillReAcknowledgedAsync(taskId, expectedAcknowledgementsAfter: 2);
            Assert.Empty(loser.Writer.Acknowledgements());
        });
    }

    /// <summary>
    /// THE RE-ACKNOWLEDGEMENT'S ENQUEUE FAILURE IS ISOLATED: with the pinned instance's channel already
    /// completed the duplicate cannot be queued, and the guarded diagnostic that reports it cannot unwind
    /// the stream even when the logger itself throws on that very message.
    /// </summary>
    [Fact]
    public async Task Duplicate_FailedReAckEnqueue_LosesNothingAndKeepsTheStream()
    {
        const string taskId = "task-dup-enqueue-fail";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            await h.CompleteOrdinaryAsync(taskId);
            h.AssignSuccessor("task-dup-enqueue-fail-successor", "successor-model");
            var successorBefore = h.SuccessorSnapshot();
            Assert.Equal(1, h.AcknowledgedCount(taskId));

            // THE CHANNEL REFUSES THE WRITE, and the diagnostic that reports it THROWS.
            Assert.True(h.Worker.MessageChannel.Writer.TryComplete());
            h.ServiceLogger.ArmThrowOnFragment(ProductionLogFragments.ReceiptAckNotQueued);
            h.ResetObservations();

            // THE DUPLICATE IS DELIVERED AND THE POST-HANDLER BARRIER IS THE TERMINAL EVENT: no
            // success line can ever arrive for a refused enqueue, so waiting for one would hang —
            // which is itself the proof that no queued claim was made.
            await h.DuplicateAndBarrierAsync(taskId, "assigned-model");

            Assert.Equal(1, h.Recorder.ConfirmCalls);
            Assert.True(h.ServiceLogger.ThrowCount > 0, "the armed diagnostic fault never fired");
            Assert.Contains(
                h.ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.ReceiptAckNotQueued, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal)
                     && m.Contains(h.Worker.Id, StringComparison.Ordinal));

            // NO SUCCESS LINE WAS CLAIMED, because nothing was queued.
            Assert.DoesNotContain(
                h.ServiceLogger.Messages,
                m => m.Contains(taskId, StringComparison.Ordinal)
                     && m.Contains(ProductionLogFragments.DuplicateReAcknowledged, StringComparison.Ordinal));

            // NOTHING WAS PROCESSED OR NOTIFIED, and the stream was not unwound by the diagnostic.
            Assert.Equal(1, h.AcknowledgedCount(taskId));
            Assert.Equal(0, h.Recorder.RecordCalls);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(successorBefore, h.SuccessorSnapshot());
            Assert.False(h.StreamEnded);

            // THE FAILED ENQUEUE LEFT THE DUPLICATE ELIGIBLE FOR REAL CONFIRMATION: the next
            // identical duplicate still ENTERS the read-only branch and performs a genuine
            // confirmation read, refused only at the (still closed) channel. A cleared eligibility
            // would have routed it down the ordinary path instead.
            await h.AssertLatestEligibleStillReachesConfirmationDespiteLostEnqueueAsync(taskId);
            Assert.Equal(0, h.Recorder.RecordCalls);
            Assert.Equal(1, h.AcknowledgedCount(taskId));
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (5) THE ONE CLASSIFICATION — ACTIVITY AND ROUTING CANNOT DIVERGE
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// DIRECTION (a) — THE TASK IS RE-DISPATCHED BETWEEN THE ACTIVITY DECISION AND HANDLER ENTRY. The
    /// one classification saw an UNHELD task, so the loop suppressed the activity refresh; a handler
    /// that re-observed would now see a live assignment and route to the ORDINARY path, releasing and
    /// notifying the NEW assignment on the strength of an OLD duplicate. The carried decision forbids
    /// that: the delivery stays a DUPLICATE.
    /// </summary>
    /// <remarks>
    /// THE ASSERTIONS ARE THE REVIEWER'S OWN CONCERN, ITEM BY ITEM: no Record settles anything for the
    /// re-dispatched assignment, it is NOT released, its queue entry is NOT removed, and nothing is
    /// notified. The single-outcome claim is then made explicitly — the classification said duplicate
    /// and the delivery was handled as a duplicate.
    /// </remarks>
    [Fact]
    public async Task Classification_TaskReDispatchedBetweenActivityAndHandler_StaysADuplicate()
    {
        const string taskId = "task-classify-redispatched";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            await h.CompleteOrdinaryAsync(taskId);
            Assert.Equal(1, h.AcknowledgedCount(taskId));

            // THE TASK IS UNHELD WHEN THE DELIVERY IS CLASSIFIED — so the arm classifies it as a
            // DUPLICATE and suppresses the activity refresh.
            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Queue.GetActiveTask(taskId));

            // A successor holds a DIFFERENT task, which is the ordinary shape a duplicate arrives in
            // and what the activity-neutral Ready barrier needs in order to be refused.
            h.AssignSuccessor("task-classify-redispatched-successor", "successor-model");
            h.ResetObservations();

            // THE RE-DISPATCH LANDS IN THE REAL PRODUCTION WINDOW: inside the WorkStream Complete arm,
            // after its activity decision and before it invokes the handler, with THIS message in
            // flight.
            var delivery = await h.DeliverThroughWorkStreamWithWindowMutationAsync(
                taskId,
                "assigned-model",
                () =>
                {
                    h.Queue.MarkComplete("task-classify-redispatched-successor");
                    h.ReactivateLatestTask(taskId);
                });

            // ── ONE DECISION GOVERNED BOTH ──────────────────────────────────────────────────
            Assert.False(
                delivery.ActivityRefreshed,
                "a delivery classified as a duplicate must not refresh the activity clock");

            // …AND THE HANDLER AGREED, even though ownership changed in the window: it took the
            // duplicate branch, proven by the confirmation read that ONLY that branch performs.
            Assert.Equal(1, h.Recorder.ConfirmCalls);

            // ── THE RE-DISPATCHED ASSIGNMENT WAS NOT TOUCHED ────────────────────────────────
            // This is the "old duplicate acts as the new task's completion" case: nothing settled
            // AlreadyStored, nothing was released or removed, and nothing was notified.
            Assert.Equal(0, h.Recorder.RecordCalls);
            Assert.True(h.Worker.IsBusy);
            Assert.Equal(taskId, h.Worker.CurrentTaskId);
            Assert.NotNull(h.Queue.GetActiveTask(taskId));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TasksEnqueued);

            // The post-read recheck refused it, so no acknowledgement was published either.
            Assert.Equal(1, h.AcknowledgedCount(taskId));
            await h.AssertNoNewAcknowledgementThroughALivePumpAsync((taskId, 1));

            // THE STREAM'S ELIGIBILITY SURVIVES A FAILED DUPLICATE: once the re-dispatch is undone,
            // an identical duplicate is re-acknowledged again through the same live stream.
            await h.AssertLatestEligibleSurvivedAndReAcknowledgesAfterReleaseAsync(
                taskId, expectedAcknowledgementsAfter: 2);
        });
    }

    /// <summary>
    /// DIRECTION (b) — THE TASK BECOMES UNHELD BETWEEN THE ACTIVITY DECISION AND HANDLER ENTRY. The one
    /// classification saw a HELD task, so the arm refreshed the activity clock; a handler that
    /// re-observed would now see an unheld task and route to the read-only DUPLICATE branch, discarding
    /// a completion the worker genuinely produced. The carried decision forbids that: the delivery
    /// stays ORDINARY.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT THE ORDINARY PATH ACTUALLY DOES HERE, STATED PRECISELY. The carried snapshot still reports
    /// the worker busy with this task, so the busy/current-task gate PASSES; the window mutation
    /// removed the active queue entry, so the path is refused at the NEXT gate — <c>GetActiveTask(...)
    /// is null</c>. It does NOT reach <c>Record</c> and it does NOT reach the checked release, and this
    /// vector does not claim otherwise.
    /// </para>
    /// <para>
    /// THE DISCRIMINATOR IS THE ROUTING, NOT THE REFUSAL. What proves ORDINARY routing is that NO
    /// confirmation read happened and NO duplicate diagnostic was emitted — the duplicate branch always
    /// does the former — combined with the activity refresh the arm performed for this same delivery.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Classification_TaskReleasedBetweenActivityAndHandler_StaysOrdinary()
    {
        const string taskId = "task-classify-released";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            // A FIRST ordinary completion populates this stream's holder, so latest-ID equality
            // holds below and the ONLY thing keeping this delivery off the duplicate branch is the
            // held state. The eligibility is proven LIVE — a duplicate right now IS re-acknowledged.
            await h.CompleteOrdinaryAsync(taskId);
            await h.AssertLatestEligibleStillReAcknowledgedAsync(taskId, expectedAcknowledgementsAfter: 2);

            // THE BASELINE, taken after that probe and before the delivery under test.
            var ignoredBefore = h.DiagnosticCount(ProductionLogFragments.DuplicateIgnored, taskId);
            var reAckedBefore =
                h.DiagnosticCount(ProductionLogFragments.DuplicateReAcknowledged, taskId);

            // THE TASK IS HELD AGAIN WHEN THE DELIVERY IS CLASSIFIED — so the arm classifies it as
            // ORDINARY and refreshes the activity clock.
            h.ReactivateLatestTask(taskId);
            h.PushActivityClockBack();
            h.ResetObservations();

            // THE RELEASE LANDS IN THE REAL PRODUCTION WINDOW, with THIS message in flight.
            var delivery = await h.DeliverThroughWorkStreamWithWindowMutationAsync(
                taskId,
                "assigned-model",
                () => h.ReleaseTask(taskId));

            // ── ONE DECISION GOVERNED BOTH ──────────────────────────────────────────────────
            Assert.True(
                delivery.ActivityRefreshed,
                "a delivery classified as ordinary must keep its normal activity refresh");

            // …AND THE HANDLER AGREED, even though the task became unheld in the window: it never
            // performed a confirmation read, which ONLY the duplicate branch does, and it emitted no
            // duplicate diagnostic of any kind.
            Assert.Equal(0, h.Recorder.ConfirmCalls);
            Assert.Equal(
                ignoredBefore, h.DiagnosticCount(ProductionLogFragments.DuplicateIgnored, taskId));
            Assert.Equal(
                reAckedBefore,
                h.DiagnosticCount(ProductionLogFragments.DuplicateReAcknowledged, taskId));

            // THE ORDINARY PATH RAN AND WAS REFUSED BY ITS OWN QUEUE-OWNERSHIP GATE. The carried
            // snapshot still says the worker is busy with this task — that is the whole point of
            // carrying it — so the busy/current-task gate PASSES, and the refusal comes at the next
            // gate: the active queue entry the window mutation removed. It is NOT a Record refusal and
            // NOT a checked-release refusal; the path never reaches either.
            Assert.Contains(
                h.ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionIgnored, StringComparison.Ordinal)
                     && m.Contains(
                         HiveOrchestratorService.OwnershipRefusalReasons.NoActiveQueueEntry,
                         StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));
            Assert.Equal(0, h.Recorder.RecordCalls);

            // NOTHING WAS ACKNOWLEDGED OR NOTIFIED by this delivery: the ledger still carries only
            // the ordinary completion's own acknowledgement and the eligibility probe's.
            Assert.Equal(2, h.AcknowledgedCount(taskId));
            await h.AssertNoNewAcknowledgementThroughALivePumpAsync((taskId, 2));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DashboardNotifications);
        });
    }

    /// <summary>
    /// THE READ LOOP ITSELF USES THE CARRIED DECISION, pinned STRUCTURALLY at the call site: its
    /// <c>Complete</c> arm classifies ONCE into a local, decides the activity refresh from THAT
    /// local's field, hands THAT SAME local to the carrying handler, and re-observes ownership
    /// NOWHERE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IT IS A SUPPLEMENT, NEVER THE EVIDENCE. The behavioural proof is
    /// <see cref="Classification_TaskReDispatchedBetweenActivityAndHandler_StaysADuplicate"/> and
    /// <see cref="Classification_TaskReleasedBetweenActivityAndHandler_StaysOrdinary"/>, which drive
    /// the REAL <c>WorkStream</c> boundary and mutate ownership inside the production window. This
    /// vector adds the one fact those cannot see from outside: that the arm names the SAME local in
    /// both expressions rather than two separately derived values that happen to agree at runtime.
    /// </para>
    /// <para>
    /// EVERY ASSERTION NAMES THE EXACT LOCAL, so a miswire that constructed a second routing value —
    /// or re-observed ownership and derived a fresh decision — fails here even if both values would
    /// have agreed in the fixtures.
    /// </para>
    /// </remarks>
    [Fact]
    public void ReadLoop_CompleteArm_CarriesTheSingleClassificationIntoTheHandler()
    {
        var source = ReadOrchestratorServiceSource();

        var arm = source.Index("case WorkerMessage.PayloadOneofCase.Complete:");
        var nextArm = source.IndexAfter("case WorkerMessage.PayloadOneofCase.ToolRequest:", arm);
        var body = StripComments(source.Text[arm..nextArm]);

        // ── ONE CLASSIFICATION, INTO ONE NAMED LOCAL ────────────────────────────────────────
        Assert.Equal(1, CountOccurrences(body, "ClassifyCompletionDelivery("));
        Assert.Equal(
            1,
            CountOccurrences(
                body,
                "var completionRouting = ClassifyCompletionDelivery(\n"
                + "                            pinnedWorker, message.Complete.TaskId, completionAckState);"));

        // ── THE ACTIVITY DECISION IS CONTROLLED BY THAT EXACT LOCAL'S FIELD ─────────────────
        Assert.Contains(
            "if (!completionRouting.IsLatestEligibleDuplicate)\n                            workerPool.TouchActivity(pinnedWorker.Id);",
            body,
            StringComparison.Ordinal);

        // …and the refresh happens ONLY under that condition: the arm holds exactly one TouchActivity.
        Assert.Equal(1, CountOccurrences(body, "TouchActivity("));

        // ── THAT EXACT LOCAL IS THE HANDLER'S ROUTING ARGUMENT ─────────────────────────────
        Assert.Contains(
            "HandleClassifiedTaskComplete(\n"
            + "                            pinnedWorker, message.Complete, completionRouting, completionAckState);",
            body,
            StringComparison.Ordinal);

        // …and the loop never routes through the classifying entry point, which would re-observe.
        Assert.DoesNotContain("HandleTaskComplete(pinnedWorker", body, StringComparison.Ordinal);

        // ── THE ELIGIBILITY HOLDER IS THE INVOCATION'S, NOT THE ARM'S ──────────────────────
        // The arm NAMES the WorkStream invocation's one local in both expressions and allocates
        // nothing of its own: a per-message allocation, a lazy fallback or a worker/service/static
        // lookup would all have to appear here.
        Assert.Equal(2, CountOccurrences(body, "completionAckState"));
        foreach (var forbiddenHolderSource in new[]
                 {
                     "new WorkStreamCompletionAckState(",   // a per-message allocation
                     "pinnedWorker.AckState",               // an attachment on the worker
                     "_completionAckState",                 // a service field
                     "??=",                                 // a lazy fallback
                 })
        {
            Assert.DoesNotContain(forbiddenHolderSource, body, StringComparison.Ordinal);
        }

        // ── NO INDEPENDENT OWNERSHIP RE-OBSERVATION ANYWHERE IN THE ARM ────────────────────
        // A second look at mutable ownership is exactly the divergence this arm exists to prevent.
        foreach (var reObservation in new[]
                 {
                     "TryGetWorkerSnapshot",
                     "GetActiveTask",
                     "GetWorker(",
                     "IsLatestEligibleDuplicate(",   // the predicate itself, re-evaluated
                     "message.Complete.TaskId, completionAckState).",
                 })
        {
            Assert.DoesNotContain(reObservation, body, StringComparison.Ordinal);
        }

        // ── THE HOLDER IS ALLOCATED EXACTLY ONCE, OUTSIDE THE READ LOOP ────────────────────
        // It is a LOCAL of the WorkStream invocation: one allocation in the whole method, textually
        // before the read loop the arm lives in, and no service field of that type anywhere.
        var workStream = StripComments(source.Between(
            "public override async Task WorkStream(", "public override Task<HeartbeatResponse> Heartbeat("));
        Assert.Equal(
            1, CountOccurrences(workStream, "var completionAckState = new WorkStreamCompletionAckState();"));
        Assert.True(
            workStream.IndexOf("var completionAckState = new WorkStreamCompletionAckState();", StringComparison.Ordinal)
            < workStream.IndexOf("await foreach (var message in requestStream", StringComparison.Ordinal),
            "the one latest-eligible holder must be allocated OUTSIDE the read loop.");
        var uncommentedService = StripComments(source.Text);
        Assert.DoesNotContain(
            "WorkStreamCompletionAckState _", uncommentedService, StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(uncommentedService, "new WorkStreamCompletionAckState("));

        // ── THE DIRECT-CALLER WRAPPER REQUIRES THE CALLER'S HOLDER ────────────────────────
        // There is exactly ONE retained wrapper, no production caller of it, and its state argument
        // is neither optional nor defaulted. That makes every reflective test caller state its own
        // ownership explicitly instead of receiving an invisible per-call fallback.
        var directWrappers = typeof(HiveOrchestratorService)
            .GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Where(method => method.Name == "HandleTaskComplete")
            .ToArray();
        var directWrapper = Assert.Single(directWrappers);
        var directParameters = directWrapper.GetParameters();
        Assert.Equal(3, directParameters.Length);
        Assert.Equal(typeof(WorkStreamCompletionAckState), directParameters[2].ParameterType);
        Assert.Equal("ackState", directParameters[2].Name);
        Assert.False(directParameters[2].IsOptional);
        Assert.False(directParameters[2].HasDefaultValue);
        Assert.Equal(1, CountOccurrences(uncommentedService, "HandleTaskComplete("));

        var directWrapperBody = StripComments(source.Between(
            "private void HandleTaskComplete(",
            "private void HandleClassifiedTaskComplete("));
        Assert.DoesNotContain(
            "new WorkStreamCompletionAckState(", directWrapperBody, StringComparison.Ordinal);
        Assert.Contains(
            "ClassifyCompletionDelivery(worker, complete.TaskId, ackState)",
            directWrapperBody,
            StringComparison.Ordinal);
        Assert.Contains("ackState);", directWrapperBody, StringComparison.Ordinal);

        // ── AND THE CARRYING HANDLER DOES NOT RE-OBSERVE EITHER, BEFORE IT ROUTES ──────────
        var handlerEntry = StripComments(source.Between(
            "private void HandleClassifiedTaskComplete(",
            "HandleLatestEligibleDuplicate(worker, complete);"));
        Assert.DoesNotContain("TryGetWorkerSnapshot", handlerEntry, StringComparison.Ordinal);
        Assert.DoesNotContain("GetActiveTask", handlerEntry, StringComparison.Ordinal);
        Assert.Contains("routing.ObservationValid", handlerEntry, StringComparison.Ordinal);
        Assert.Contains("routing.Observed", handlerEntry, StringComparison.Ordinal);
        Assert.Contains("routing.IsLatestEligibleDuplicate", handlerEntry, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE STALE DUPLICATE CANNOT SETTLE A REDISPATCH THAT LANDS WHILE IT IS IN FLIGHT. The stale
    /// Complete is classified as a duplicate (the task is unheld at that instant), and the SAME task id
    /// is RE-DISPATCHED inside the production window — after the arm's activity decision, before it
    /// invokes the handler. The in-flight duplicate must never become the new assignment's completion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THE INTERLEAVING MATTERS, AND WHY IT IS THE ONLY HONEST SHAPE. The retained evidence is
    /// IDENTICAL, so a delivery that reached the ordinary path would have had its <c>Record</c> settle
    /// <c>AlreadyStored</c> — which the ordinary path ACCEPTS — and would then have released the NEW
    /// assignment, removed its queue entry and notified it. Redispatching AFTER the stale delivery had
    /// already been fully handled would prove nothing, because the new assignment would not exist while
    /// the delivery was classified or handled. Here it exists from the window onwards.
    /// </para>
    /// <para>
    /// THE NEW ASSIGNMENT IS THEN PROVEN STILL LIVE AND STILL COMPLETABLE: its own completion is
    /// delivered afterwards and IS accepted, so the vector shows the stale duplicate neither consumed
    /// nor corrupted it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Duplicate_StaleDeliveryAgainstAReDispatchedTask_NeverSettlesTheNewAssignment()
    {
        const string taskId = "task-stale-vs-redispatch";

        var h = AckHarness.Create();
        await AckHarness.RunAsync(h, async () =>
        {
            await h.CompleteOrdinaryAsync(taskId);
            Assert.Equal(1, h.AcknowledgedCount(taskId));
            Assert.NotNull(h.Stores.ReceiptStore.Load(taskId));

            // ── THE STALE DUPLICATE IS CLASSIFIED WHILE THE TASK IS GENUINELY UNHELD ─────────
            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Queue.GetActiveTask(taskId));

            // A successor holds a DIFFERENT task, which is the ordinary shape a stale duplicate
            // arrives in and what the activity-neutral Ready barrier needs in order to be refused.
            h.AssignSuccessor("task-stale-vs-redispatch-successor", "successor-model");
            h.ResetObservations();

            // ── THE REDISPATCH LANDS DURING THAT SAME INBOUND COMPLETE ───────────────────────
            // Inside the real WorkStream window: after the activity decision, before handler entry.
            var delivery = await h.DeliverThroughWorkStreamWithWindowMutationAsync(
                taskId,
                "assigned-model",
                () =>
                {
                    h.Queue.MarkComplete("task-stale-vs-redispatch-successor");
                    h.ReactivateLatestTask(taskId);
                });

            // The arm classified it as a duplicate, so it withheld the activity refresh.
            Assert.False(delivery.ActivityRefreshed);

            // ── ZERO RECORD: NOTHING SETTLED AlreadyStored FOR THE NEW ASSIGNMENT ────────────
            Assert.Equal(0, h.Recorder.RecordCalls);

            // It stayed on the read-only branch, which is the only path that reads.
            Assert.Equal(1, h.Recorder.ConfirmCalls);

            // ── NO RELEASE, NO REMOVAL, NO NOTIFICATION ─────────────────────────────────────
            Assert.True(h.Worker.IsBusy);
            Assert.Equal(taskId, h.Worker.CurrentTaskId);
            Assert.NotNull(h.Queue.GetActiveTask(taskId));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TasksEnqueued);

            // ── NO NEW ACK, after the handler barrier AND a pump observation barrier ─────────
            // The post-read recheck saw the redispatch and refused, so nothing was published.
            Assert.Equal(1, h.AcknowledgedCount(taskId));
            await h.AssertNoNewAcknowledgementThroughALivePumpAsync((taskId, 1));

            // ── AND THE RE-DISPATCHED ASSIGNMENT IS STILL COMPLETABLE ORDINARILY ─────────────
            // It reuses the same opaque id, so the recorder settles AlreadyStored on the identical
            // retained evidence — which the ORDINARY path accepts, releases and notifies. That
            // disposition is reachable only because the stale duplicate never consumed it.
            h.ResetObservations();
            var activityAtAcceptance = await h.CompleteHeldTaskOrdinarilyAsync(taskId);
            Assert.True(activityAtAcceptance > DateTime.MinValue);

            Assert.Equal(1, h.Recorder.RecordCalls);
            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Queue.GetActiveTask(taskId));
            Assert.Equal(1, h.TransportNotifications);
            Assert.Equal(1, h.DashboardNotifications);
            Assert.Equal(2, h.AcknowledgedCount(taskId));
        });
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static ServerCallContext MockContext() => new Mock<ServerCallContext>().Object;

    /// <summary>
    /// Strips <c>//</c> line comments, so the structural assertions read CODE rather than prose.
    /// </summary>
    /// <remarks>
    /// THE ARM IS HEAVILY COMMENTED, and its comments legitimately mention the very identifiers the
    /// assertions forbid (for example the explanation of why re-observing is wrong). Without this, a
    /// "no re-observation" assertion could fail on a comment, or — far worse — a "the code says X"
    /// assertion could pass because a COMMENT said X.
    /// </remarks>
    /// <param name="code">The source fragment.</param>
    /// <returns>The fragment with line comments removed and its line structure preserved.</returns>
    private static string StripComments(string code) =>
        string.Join(
            '\n',
            code.Split('\n').Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment < 0 ? line : line[..comment].TrimEnd();
            }));

    /// <summary>Counts non-overlapping occurrences of <paramref name="needle"/>.</summary>
    /// <param name="haystack">The text to search.</param>
    /// <param name="needle">The literal to count.</param>
    /// <returns>The number of occurrences.</returns>
    private static int CountOccurrences(string haystack, string needle)    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// THE PRODUCTION SOURCE of <c>HiveOrchestratorService</c>, for the structural call-site vector.
    /// </summary>
    private sealed class OrchestratorSource
    {
        /// <summary>The full file text.</summary>
        public required string Text { get; init; }

        /// <summary>The index of <paramref name="needle"/>; a miss is a named failure.</summary>
        /// <param name="needle">The literal to locate.</param>
        /// <returns>The index.</returns>
        public int Index(string needle)
        {
            var i = Text.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(i >= 0, $"the production source no longer contains '{needle}'.");
            return i;
        }

        /// <summary>The index of <paramref name="needle"/> at or after <paramref name="from"/>.</summary>
        /// <param name="needle">The literal to locate.</param>
        /// <param name="from">Where to start searching.</param>
        /// <returns>The index.</returns>
        public int IndexAfter(string needle, int from)
        {
            var i = Text.IndexOf(needle, from, StringComparison.Ordinal);
            Assert.True(i >= 0, $"the production source no longer contains '{needle}' after {from}.");
            return i;
        }

        /// <summary>The text between two literals, both of which must exist in order.</summary>
        /// <param name="start">The opening literal.</param>
        /// <param name="end">The closing literal, searched after <paramref name="start"/>.</param>
        /// <returns>The enclosed text.</returns>
        public string Between(string start, string end)
        {
            var from = Index(start);
            return Text[from..IndexAfter(end, from)];
        }
    }

    /// <summary>
    /// Loads <c>HiveOrchestratorService.cs</c> by walking up from the test assembly to the repository
    /// root, so the structural vector reads the real file wherever the run was started.
    /// </summary>
    /// <remarks>
    /// A MISSING FILE IS A LOUD FAILURE, never a silently skipped assertion: a structural vector that
    /// cannot find its subject proves nothing and must say so.
    /// </remarks>
    /// <returns>The production source.</returns>
    private static OrchestratorSource ReadOrchestratorServiceSource()
    {
        const string relative = "src/CopilotHive/Services/HiveOrchestratorService.cs";

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return new OrchestratorSource { Text = File.ReadAllText(candidate) };

            directory = directory.Parent;
        }

        Assert.Fail(
            $"'{relative}' was not found walking up from '{AppContext.BaseDirectory}'; the structural " +
            "call-site vector cannot be evaluated.");
        return null!;
    }


    // ═══════════════════════════════════════════════════════════════════════
    //  (4c) THE COMPLETION-PUBLICATION SELECTION HOLD'S ORDER AND SCOPE
    //      (structural: the production completion path's statement order)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE COMPLETION PUBLICATION'S EXACT SHAPE, read off the production source: the release acquires
    /// the hold only on the negotiated path, everything after the acquisition sits inside ONE
    /// try/finally, the hold ends only for the invocation that acquired it, and the ordinary
    /// notification follows the hold's end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY A STRUCTURAL VECTOR IS HONEST HERE. The properties it asserts are STATEMENT-ORDER
    /// properties of one synchronous method — "is every operation after the acquisition inside the
    /// guarded scope", "is the hold's end before the notification", "is the hold's end conditional on
    /// having acquired one". Behavioural vectors (this suite and the transport ownership suite) prove
    /// the effects; this one proves the SHAPE, so a future edit that hoists the clear out of the
    /// finally, drops the opt-in gate or moves the notification inside the guarded scope fails loudly
    /// rather than passing on a happy path.
    /// </para>
    /// <para>
    /// IT IS NARROW: it names the exact identifiers it expects, so it cannot be satisfied by a
    /// different arrangement that happens to contain the same words.
    /// </para>
    /// </remarks>
    [Fact]
    public void CompletionPublicationHold_TheAcquisitionGateScopeAndOrderAreTheContract()
    {
        var source = ReadOrchestratorServiceSource();
        var handler = StripComments(source.Between(
            "private void HandleClassifiedTaskComplete(",
            "private void LogCompletionNotRecorded("));

        // ── (1) THE OPT-IN GATE: the hold is requested ONLY for a NEGOTIATED registration ───────
        Assert.Contains(
            "var holdForCompletionPublication = worker.CompletionReceiptAckEnabled;",
            handler,
            StringComparison.Ordinal);

        // …and it selects between the two release routes on that exact condition: the holding route
        // for the negotiated path, and the PRESERVED route for everything else.
        Assert.Contains(
            "var completionPublicationHeld = holdForCompletionPublication\n"
            + "            ? workerPool.TryReleaseCompletedTaskHoldingForPublication(worker, complete.TaskId)\n"
            + "            : ApplyTaskCompletion(worker, complete.TaskId);",
            handler,
            StringComparison.Ordinal);

        // ── (2) THE RELEASE REFUSAL RETURNS BEFORE ANY HOLD END COULD RUN ──────────────────────
        var refusalIndex = handler.IndexOf(
            "OwnershipRefusalReasons.CheckedReleaseRefused", StringComparison.Ordinal);
        Assert.True(refusalIndex >= 0, "the checked-release refusal reason is gone from the handler.");

        // ── (3) ONE GUARDED SCOPE COVERS EVERY OPERATION AFTER THE ACQUISITION ─────────────────
        // THE ANCHOR IS THE ACQUISITION ITSELF, NOT A FIRST TEXTUAL MATCH. `HandleClassifiedTaskComplete`
        // contains SEVERAL earlier `try` blocks (the boundary mapping and the recorder), so anchoring on
        // the first "        try" would select one of THOSE and the membership/ordering assertions below
        // would be satisfied by a publication whose operations had escaped their guarded scope entirely.
        // The publication's `try` is located strictly AFTER the refusal return, and its extent is
        // computed by BALANCED BRACE MATCHING rather than by the next textual "finally".
        var releaseIndex = handler.IndexOf(
            "var completionPublicationHeld = holdForCompletionPublication", StringComparison.Ordinal);
        Assert.True(releaseIndex >= 0, "the guarded release acquisition is gone from the handler.");
        Assert.True(
            releaseIndex < refusalIndex,
            "the checked release must be attempted BEFORE its refusal return is evaluated.");

        var tryIndex = handler.IndexOf("\n        try\n", refusalIndex, StringComparison.Ordinal);
        Assert.True(
            tryIndex >= 0,
            "no guarded scope follows the checked release's refusal return — the publication's own "
            + "try/finally is gone.");

        // THE TWO BODIES, BY BALANCED BRACES: everything the publication does must live inside the
        // FIRST, and the hold's end inside the SECOND.
        var (tryBodyStart, tryBodyEnd) = BraceScopedBody(handler, tryIndex);
        var finallyIndex = handler.IndexOf("\n        finally\n", tryBodyEnd, StringComparison.Ordinal);
        Assert.True(
            finallyIndex >= 0,
            "the publication's guarded scope has no `finally` — the hold could not be ended on every path.");
        var (finallyBodyStart, finallyBodyEnd) = BraceScopedBody(handler, finallyIndex);

        var guardedScope = handler[tryBodyStart..tryBodyEnd];
        var finallyBlock = handler[finallyBodyStart..finallyBodyEnd];

        // EVERY POST-ACQUISITION OPERATION IS *INSIDE* THE GUARDED SCOPE. Moving any one of them
        // before the `try` (or after the `finally`) fails here, which is the mandatory requirement:
        // a removal, hook, eligibility or enqueue fault must never strand the hold.
        foreach (var (name, statement) in new[]
                 {
                     ("the active-queue removal", "taskQueue.MarkComplete(complete.TaskId)"),
                     ("the post-release window",
                         "_afterCompletionReleaseBeforeAckForTest?.Invoke(worker, complete.TaskId)"),
                     ("the eligibility advance", "ackState.AdvanceLatestEligible(complete.TaskId)"),
                     ("the acknowledgement enqueue",
                         "TryPublishCompletionReceiptAck(worker, complete.TaskId)"),
                 })
        {
            Assert.True(
                guardedScope.Contains(statement, StringComparison.Ordinal),
                $"{name} is not inside the completion publication's try/finally scope, so a fault "
                + "there would leave the selection hold installed.");

            // …and it appears EXACTLY ONCE in the whole handler, so the membership above cannot be
            // satisfied by a copy inside the scope while the live statement sits outside it.
            Assert.Equal(1, CountOccurrences(handler, statement));
        }

        // THE ORDER INSIDE THE SCOPE IS STILL THE CONTRACT.
        var queueRemovalIndex = handler.IndexOf(
            "taskQueue.MarkComplete(complete.TaskId)", StringComparison.Ordinal);
        var windowIndex = handler.IndexOf(
            "_afterCompletionReleaseBeforeAckForTest?.Invoke(worker, complete.TaskId)",
            StringComparison.Ordinal);
        var eligibilityIndex = handler.IndexOf(
            "ackState.AdvanceLatestEligible(complete.TaskId)", StringComparison.Ordinal);
        var enqueueIndex = handler.IndexOf(
            "TryPublishCompletionReceiptAck(worker, complete.TaskId)", StringComparison.Ordinal);
        var clearIndex = handler.IndexOf(
            "workerPool.ClearCompletionPublicationHold(worker);", StringComparison.Ordinal);
        var notificationIndex = handler.IndexOf(
            "_dashboardNotifier?.NotifyStateChanged();", StringComparison.Ordinal);

        foreach (var (name, index) in new[]
                 {
                     ("the hold's end", clearIndex),
                     ("the dashboard notification", notificationIndex),
                 })
        {
            Assert.True(index >= 0, $"{name} is missing from the completion publication.");
        }

        Assert.True(
            releaseIndex < refusalIndex && refusalIndex < tryIndex
            && tryIndex < queueRemovalIndex && queueRemovalIndex < windowIndex
            && windowIndex < eligibilityIndex && eligibilityIndex < enqueueIndex
            && enqueueIndex < finallyIndex && finallyIndex < clearIndex
            && clearIndex < notificationIndex,
            "the completion publication's order changed: the release, its refusal return, the guarded "
            + "scope, the queue removal, the window, the eligibility, the enqueue, the hold's end and "
            + "the ordinary notification must appear in exactly that order.");

        // ── (4) THE HOLD'S END IS INSIDE THE `finally` AND CONDITIONAL ON HAVING ACQUIRED ONE ──
        // A refusal (or a legacy registration) must never clear a hold this invocation did not take,
        // and the clear must be reached on EVERY path out of the scope — including an exception.
        Assert.True(
            finallyBlock.Contains("workerPool.ClearCompletionPublicationHold(worker);", StringComparison.Ordinal),
            "the hold's end is not inside the publication's `finally`, so an exception would strand it.");
        Assert.Contains(
            "if (holdForCompletionPublication)",
            finallyBlock,
            StringComparison.Ordinal);

        // ── (5) THE ORDINARY NOTIFICATION IS OUTSIDE THE GUARDED SCOPE ─────────────────────────
        // A notification fault must not be able to reach the hold's end, and nothing is notified while
        // the publication's scope is still open.
        Assert.True(
            notificationIndex > finallyBodyEnd,
            "the ordinary dashboard notification must follow the END of the publication's guarded "
            + "scope, not merely the textual position of the hold's end.");
        Assert.DoesNotContain(
            "_dashboardNotifier?.NotifyStateChanged", guardedScope, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_dashboardNotifier?.NotifyStateChanged", finallyBlock, StringComparison.Ordinal);

        // ── (6) NO DATABASE/LOGGER/CALLBACK WORK INSIDE THE RELEASE CALL ITSELF ────────────────
        // The only calls the publication makes before the guarded scope are the two release routes;
        // the pool owns the lock, so nothing held here can deadlock against a logger or a store.
        Assert.Equal(1, CountOccurrences(handler, "TryReleaseCompletedTaskHoldingForPublication("));
        Assert.Equal(1, CountOccurrences(handler, "ClearCompletionPublicationHold("));
    }

    /// <summary>
    /// THE SELECTION HOLD'S THREE POOL-SIDE EFFECTS, read off the production source with BRACE-SCOPED
    /// extraction: the idle-selection predicate is the COMBINED idle-and-not-held test evaluated
    /// INSIDE the activity lock's body, the hold's INSTALLATION sits inside the checked release's own
    /// lock body, and the checked Ready idle refuses while the hold is active.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IT COMPLEMENTS THE BEHAVIOURAL WORKER-POOL VECTORS: they prove the effects, this one proves the
    /// SHAPE — in particular that the two facts are read as ONE predicate INSIDE ONE lock body rather
    /// than as two independent unlocked reads, which is exactly the difference between a real exclusion
    /// and a window.
    /// </para>
    /// <para>
    /// WHY BRACE SCOPE AND NOT TEXT ORDER. A bare "the lock keyword appears before the predicate"
    /// assertion is satisfied by an EMPTY lock followed by an unlocked predicate, and by a
    /// <c>BeginCompletionPublicationHold</c> call moved to AFTER the release's lock body closes — both
    /// of which reintroduce the forbidden selectable interval. Binding each statement to the matched
    /// BODY of its own <c>lock (_activityLock)</c> is what makes those mutations fail.
    /// </para>
    /// </remarks>
    [Fact]
    public void CompletionPublicationHold_TheSelectionPredicateAndReadyRefusalAreTheContract()
    {
        var poolSource = ReadSourceFile("src/CopilotHive/Services/WorkerPool.cs");
        var pool = new OrchestratorSource { Text = StripComments(poolSource) };

        // ── (1) THE COMBINED PREDICATE LIVES INSIDE GetIdleWorker's OWN LOCK BODY ──────────────
        // The predicate is the pool's ONE selectability expression — idle, not publishing and not
        // awaiting its own accepted Ready — evaluated as a single call so the three facts cannot be
        // read at three different instants.
        const string combinedPredicate =
            "if (IsSelectableIdleNoLock(kvp.Value))";

        var selection = pool.Between(
            "public ConnectedWorker? GetIdleWorker()", "public IReadOnlyList<ConnectedWorker> GetAllWorkers()");
        Assert.Contains(combinedPredicate, selection, StringComparison.Ordinal);

        var selectionLockBody = LockBodyOf(selection, "GetIdleWorker");
        Assert.True(
            selectionLockBody.Contains(combinedPredicate, StringComparison.Ordinal),
            "the combined idle-and-not-held predicate must be evaluated INSIDE the activity lock's "
            + "body: an empty lock followed by an unlocked read is exactly the torn observation this "
            + "predicate exists to prevent.");

        // …and there is exactly ONE such predicate and ONE lock in the method, so the membership above
        // cannot be satisfied by a second, unlocked copy doing the real selection.
        Assert.Equal(1, CountOccurrences(selection, combinedPredicate));
        Assert.Equal(1, CountOccurrences(selection, "lock (_activityLock)"));

        // The selection also returns ONLY from inside that lock body — a return placed after the lock
        // closes would re-open the very window under test.
        Assert.Equal(2, CountOccurrences(selectionLockBody, "return "));
        Assert.Equal(2, CountOccurrences(selection, "return "));

        // ── (2) THE HOLD'S INSTALLATION LIVES INSIDE THE CHECKED RELEASE'S OWN LOCK BODY ───────
        // This is the atomicity requirement itself: the idle reset and the hold must be applied in
        // ONE lock span. A BeginCompletionPublicationHold moved AFTER the release's lock body — which
        // a post-condition-only test cannot see — fails here.
        const string holdInstallation = "expected.BeginCompletionPublicationHold();";

        var release = pool.Between(
            "private bool ReleaseCompletedTaskCore(", "internal bool ClearCompletionPublicationHold(");
        Assert.Contains(holdInstallation, release, StringComparison.Ordinal);

        var releaseLockBody = LockBodyOf(release, "ReleaseCompletedTaskCore");
        foreach (var (name, statement) in new[]
                 {
                     ("the idle reset", "ResetToIdleNoLock(expected);"),
                     ("the model clear", "expected.CurrentModel = null;"),
                     ("the hold's installation", holdInstallation),
                 })
        {
            Assert.True(
                releaseLockBody.Contains(statement, StringComparison.Ordinal),
                $"{name} must be applied INSIDE the checked release's activity-lock body, so the "
                + "release and the hold are ONE observable step with no selectable interval between "
                + "them.");
        }

        // …exactly once each, so none of them can also exist outside the lock body.
        Assert.Equal(1, CountOccurrences(release, holdInstallation));
        Assert.Equal(1, CountOccurrences(release, "lock (_activityLock)"));

        // AND IT IS GATED ON THE OPT-IN, so the preserved route installs nothing.
        Assert.Contains(
            "if (holdForCompletionPublication)\n                expected.BeginCompletionPublicationHold();",
            releaseLockBody,
            StringComparison.Ordinal);

        // ── (3) THE HOLD'S END ALSO HAPPENS UNDER THE LOCK, FOR THE EXACT REGISTERED INSTANCE ──
        var clear = pool.Between(
            "internal bool ClearCompletionPublicationHold(", "internal bool TryMarkIdleForReady(");
        var clearLockBody = LockBodyOf(clear, "ClearCompletionPublicationHold");
        Assert.True(
            clearLockBody.Contains("expected.EndCompletionPublicationHold();", StringComparison.Ordinal),
            "the hold's end must happen under the activity lock.");
        Assert.True(
            clearLockBody.Contains("ReferenceEquals(registered, expected)", StringComparison.Ordinal),
            "the hold's end must re-check the exact registered instance under the same lock, so an "
            + "ABA replacement is never mutated.");

        // ── (4) THE CHECKED READY IDLE REFUSES WHILE HELD ─────────────────────────────────────
        var ready = pool.Between(
            "internal bool TryMarkIdleForReady(", "private bool IsStillOwnedNoLock(");
        Assert.Contains(
            "if (expected.CompletionPublicationPending)\n                return false;",
            ready,
            StringComparison.Ordinal);

        var readyLockBody = LockBodyOf(ready, "TryMarkIdleForReady");
        Assert.True(
            readyLockBody.Contains(
                "if (expected.CompletionPublicationPending)", StringComparison.Ordinal),
            "the Ready refusal must be evaluated under the activity lock.");

        // AND THE IDLE RESET DOES NOT ERASE THE HOLD: the shared field set is exactly the assignment
        // fields.
        var reset = pool.Between(
            "private static void ResetToIdleNoLock(", "internal bool TryGetWorkerSnapshot(");
        Assert.DoesNotContain("CompletionPublication", reset, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extracts the BODY of the first <c>lock (_activityLock)</c> statement in
    /// <paramref name="methodText"/>, delimited by BALANCED BRACES rather than by the next textual
    /// closing brace.
    /// </summary>
    /// <remarks>
    /// THE BALANCED MATCH IS THE WHOLE POINT: a lock body contains nested blocks (loops, ifs), so a
    /// naive "up to the next `}`" slice would end at the first inner block and would then happily
    /// report that a statement sitting OUTSIDE the lock is inside it.
    /// </remarks>
    /// <param name="methodText">The comment-stripped method text to search.</param>
    /// <param name="methodName">The method's name, for a legible failure message.</param>
    /// <returns>The text between the lock body's outermost braces.</returns>
    private static string LockBodyOf(string methodText, string methodName)
    {
        var lockIndex = methodText.IndexOf("lock (_activityLock)", StringComparison.Ordinal);
        Assert.True(
            lockIndex >= 0,
            $"'{methodName}' no longer takes the activity lock at all.");

        var (start, end) = BraceScopedBody(methodText, lockIndex);
        return methodText[start..end];
    }

    /// <summary>
    /// Returns the half-open range of the block body that OPENS at the first <c>{</c> at or after
    /// <paramref name="from"/>, matched by BALANCED BRACE COUNTING.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THIS EXISTS. Every structural claim in this file's hold vectors is a SCOPE claim — "this
    /// statement is inside that try/lock body" — and a scope claim cannot be made with
    /// <c>IndexOf</c> ordering alone: a first textual match can select an EARLIER, unrelated block,
    /// and a naive end-delimiter can stop at the first nested block's closing brace. Both mistakes
    /// make a scope assertion pass for code that escaped the scope.
    /// </para>
    /// <para>
    /// IT IS A TEST-SIDE READER ONLY: it parses the comment-stripped production text and mutates
    /// nothing. Brace counting is sufficient here because the scanned bodies contain no braces inside
    /// string or character literals; an unbalanced input is a LOUD failure rather than a silent
    /// truncation.
    /// </para>
    /// </remarks>
    /// <param name="text">The comment-stripped source text.</param>
    /// <param name="from">The index at or after which the block's opening brace is found.</param>
    /// <returns>The body's start (just after <c>{</c>) and end (at the matching <c>}</c>).</returns>
    private static (int Start, int End) BraceScopedBody(string text, int from)
    {
        var open = text.IndexOf('{', from);
        Assert.True(open >= 0, "no block body opens after the anchor; the production shape changed.");

        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return (open + 1, i);
            }
        }

        Assert.Fail("the block body opened at the anchor is never closed; the production shape changed.");
        return default;
    }

    /// <summary>
    /// Loads a repository file by its repository-relative path, walking up from the test assembly to
    /// the repository root. A MISSING FILE IS A LOUD FAILURE, never a silently skipped assertion.
    /// </summary>
    /// <param name="relative">The repository-relative path.</param>
    /// <returns>The file's text.</returns>
    private static string ReadSourceFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        Assert.Fail(
            $"'{relative}' was not found walking up from '{AppContext.BaseDirectory}'; the structural "
            + "vector cannot be evaluated.");
        return null!;
    }

    /// <summary>
    /// THE PRODUCTION LOG FRAGMENTS these duplicate vectors synchronize on. Kept together so a wording
    /// change in production surfaces as one obvious edit rather than as scattered flaky waits.
    /// </summary>
    private static class ProductionLogFragments
    {
        /// <summary>The acknowledgement-enqueue diagnostic's stable fragment.</summary>
        public const string ReceiptAckNotQueued = "acknowledgement for task";

        /// <summary>
        /// The guarded duplicate-refusal warning's stable fragment: the duplicate was refused, so no
        /// re-acknowledgement was published and nothing was processed again.
        /// </summary>
        public const string DuplicateIgnored = "duplicate completion for latest eligible task";

        /// <summary>
        /// The re-acknowledgement SUCCESS line, emitted ONLY once the confirmation said the evidence
        /// matches, the post-read eligibility recheck passed AND the message was really QUEUED. Queue
        /// acceptance is not delivery, which is why the wording claims queuing and nothing more.
        /// </summary>
        public const string DuplicateReAcknowledged = "re-acknowledgement queued for task";

        /// <summary>The ordinary completion's acceptance provenance line.</summary>
        public const string CompletionAccepted = "completed by";

        /// <summary>The ordinary completion-refusal warning's stable fragment.</summary>
        public const string CompletionIgnored = "completion for task";

        /// <summary>The mapping-failure warning's stable fragment.</summary>
        public const string MappingFailed = "could not be mapped";

        /// <summary>The post-handler barrier's Progress line.</summary>
        public const string Progress = "progress from";
    }

    /// <summary>
    /// THE ONE REGISTRATION FIXTURE, with the completion recorder OPTIONAL because the recorder IS
    /// the negotiating party: a service without one must never enable an acknowledgement, no matter
    /// what a worker asks for.
    /// </summary>
    /// <param name="withRecorder">
    /// Whether the service is configured with a real completion recorder — the capability that makes
    /// agreement possible at all.
    /// </param>
    /// <returns>The service under test and the pool it publishes into.</returns>
    private static (HiveOrchestratorService Service, WorkerPool Pool) CreateService(
        bool withRecorder = false)
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
            NullLogger<HiveOrchestratorService>.Instance,
            completionRecorder: withRecorder ? new AgreementRecordingRecorder() : null);

        return (service, pool);
    }

    /// <summary>
    /// THE NARROW RECORDER STUB these registration vectors need: they assert what REGISTRATION
    /// decides, never what a completion records, so this stands in for the configured capability and
    /// does nothing else. It is deliberately NOT a fake that could be mistaken for real recording
    /// evidence — the behavioural recording is exercised by the transport harness over the REAL
    /// recorder.
    /// </summary>
    private sealed class AgreementRecordingRecorder : IWorkerCompletionRecorder
    {
        /// <inheritdoc />
        public void Record(string workerId, WorkTask task, TaskResult result) =>
            throw new InvalidOperationException(
                "this fixture is a REGISTRATION-CAPABILITY stub; it records no completions.");

        /// <inheritdoc />
        public bool ConfirmStoredReceipt(string workerId, string taskId, TaskResult result) => false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  (4)'s HARNESS — the real Register -> WorkStream route over real stores
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE DUPLICATE BRANCH'S DETERMINISTIC HARNESS: a REAL <see cref="HiveOrchestratorService"/> over
    /// real collaborators, the worker registered through the REAL <see cref="HiveOrchestratorService.Register"/>
    /// RPC, a REAL <c>WorkStream</c> pump, REAL insert-once stores, and a confirmation-forwarding
    /// decorator over the REAL <see cref="WorkerCompletionRecorder"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE DECORATOR IS A FORWARDER, NOT A STUB. It delegates BOTH operations to the real recorder, so
    /// the durable evidence the re-acknowledgement is authorized against is genuinely production-written
    /// — and the only thing it adds is OBSERVATION plus the two controlled seams a vector needs (a
    /// one-shot confirmation fault and a synchronous mutation immediately before the confirmation
    /// returns). A default-<c>false</c> fake would make every "no acknowledgement" assertion pass
    /// vacuously, which is precisely what this shape avoids.
    /// </para>
    /// <para>
    /// AWAITING IS DETERMINISTIC AND BOUNDED. Refusals synchronize on the production diagnostic the
    /// guarded branch emits, successes on the production success line, and both are followed by the
    /// POST-HANDLER BARRIER: the read loop handles messages strictly sequentially and the handler is
    /// awaited inline, so the barrier's own Progress message can only be processed after the handler
    /// RETURNED. No sleeps, no competing channel reader, and every started producer is joined.
    /// </para>
    /// </remarks>
    private sealed class AckHarness
    {
        /// <summary>Bound applied to every await; a hang becomes a named failure, never a stall.</summary>
        private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);

        private const string WorkerId = "ack-duplicate-worker";

        public required HiveOrchestratorService Service { get; init; }
        public required WorkerPool Pool { get; init; }
        public required TaskQueue Queue { get; init; }
        public required ConnectedWorker Worker { get; init; }
        public required FaultInjectingConfirmRecorder Recorder { get; init; }
        public required AckStores Stores { get; init; }
        public required SignallingLogger ServiceLogger { get; init; }
        public required DuplicateObservingWriter Writer { get; init; }

        private ChannelStreamReader Reader { get; init; } = null!;

        private Task StreamTask { get; init; } = null!;

        private readonly List<SecondStream> _extraStreams = [];

        private int _dashboardNotifications;
        private int _transportNotifications;
        private int _tasksEnqueued;
        private int _barrierSequence;

        private AckHarness() { }

        /// <summary>How many REAL dashboard state-change notifications were raised.</summary>
        public int DashboardNotifications => Volatile.Read(ref _dashboardNotifications);

        /// <summary>Zeroes ONLY the dashboard counter, so a nested setup step's own notification can
        /// be excluded from an outer assertion without disturbing any other observation.</summary>
        public void ResetDashboardNotifications() =>
            Interlocked.Exchange(ref _dashboardNotifications, 0);

        /// <summary>How many domain results the transport published on the shared notifier.</summary>
        public int TransportNotifications => Volatile.Read(ref _transportNotifications);

        /// <summary>How many tasks the REAL queue accepted — the "did anything advance" probe.</summary>
        public int TasksEnqueued => Volatile.Read(ref _tasksEnqueued);

        /// <summary>Whether the real stream task has terminated.</summary>
        public bool StreamEnded => StreamTask.IsCompleted;

        /// <summary>
        /// THE WORKER ID THE PINNED INSTANCE CARRIES, so the production diagnostics can be attributed
        /// to it rather than to a literal the harness might drift from.
        /// </summary>
        public string WorkerIdValue => Worker.Id;

        /// <summary>
        /// How many acknowledgements the REAL pump forwarded FOR <paramref name="taskId"/>, counted at
        /// the gRPC writer the pump writes to.
        /// </summary>
        /// <param name="taskId">The opaque task id the acknowledgement must name.</param>
        /// <returns>The count of forwarded acknowledgements for that exact id.</returns>
        public int AcknowledgedCount(string taskId) =>
            Writer.Acknowledgements().Count(
                a => string.Equals(a.TaskId, taskId, StringComparison.Ordinal));

        /// <summary>
        /// Zeroes the OBSERVATION counters and the recorder's call counters, so a vector can assert a
        /// duplicate delivery's own effects in isolation.
        /// </summary>
        /// <remarks>
        /// THE WRITER'S LEDGER IS DELIBERATELY NOT CLEARED: it is CUMULATIVE evidence, so
        /// <see cref="AcknowledgedCount"/> keeps reporting the ordinary completion's own
        /// acknowledgement alongside any re-acknowledgement. Counting from zero would make "exactly one
        /// acknowledgement for this task" impossible to distinguish from "one acknowledgement was added
        /// for a different task".
        /// </remarks>
        public void ResetObservations()
        {
            Interlocked.Exchange(ref _dashboardNotifications, 0);
            Interlocked.Exchange(ref _transportNotifications, 0);
            Interlocked.Exchange(ref _tasksEnqueued, 0);
            Recorder.ResetCounts();
        }

        /// <summary>
        /// Creates a harness whose worker negotiated the acknowledgement through the REAL registration
        /// RPC (unless <paramref name="enabled"/> is false, in which case the request is absent).
        /// </summary>
        /// <param name="enabled">Whether the registration explicitly requests the acknowledgement.</param>
        /// <returns>The live harness.</returns>
        public static AckHarness Create(bool enabled = true)
        {
            var pool = new WorkerPool();
            var queue = new TaskQueue();
            var pipelineManager = new GoalPipelineManager();
            var dashboard = new DashboardNotifier();
            var completionNotifier = new TaskCompletionNotifier();

            var goalManager = new GoalManager();
            var serviceLogger = new SignallingLogger();

            var dispatcher = new GoalDispatcher(
                goalManager,
                pipelineManager,
                queue,
                new GrpcWorkerGateway(pool),
                completionNotifier,
                NullLogger<GoalDispatcher>.Instance,
                new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

            // ── THE REAL STORES: production insert-once primitives over REAL SQLite ──────────────
            var stores = AckStores.Create();
            var recorder = new FaultInjectingConfirmRecorder(
                new WorkerCompletionRecorder(stores.AssignmentStore, stores.ReceiptStore));

            var service = new HiveOrchestratorService(
                pool,
                queue,
                pipelineManager,
                completionNotifier,
                dispatcher,
                serviceLogger,
                dashboardNotifier: dashboard,
                completionRecorder: recorder);

            // ── THE REAL REGISTRATION RPC, so the enablement is PRODUCTION's answer ──────────────
            var response = service.Register(
                new RegisterRequest
                {
                    WorkerId = WorkerId,
                    RequestCompletionReceiptAck = enabled,
                },
                MockContext()).GetAwaiter().GetResult();

            Assert.True(response.Accepted);
            Assert.Equal(enabled, response.CompletionReceiptAckEnabled);

            var worker = pool.GetWorker(WorkerId)
                ?? throw new InvalidOperationException(
                    $"the registration RPC did not publish '{WorkerId}' in the pool.");
            Assert.Equal(enabled, worker.CompletionReceiptAckEnabled);

            var reader = new ChannelStreamReader();
            var writer = new DuplicateObservingWriter();
            var streamTask = service.WorkStream(reader, writer, MockContext());

            var harness = new AckHarness
            {
                Service = service,
                Pool = pool,
                Queue = queue,
                Worker = worker,
                Recorder = recorder,
                Stores = stores,
                ServiceLogger = serviceLogger,
                Writer = writer,
                Reader = reader,
                StreamTask = streamTask,
            };

            dashboard.OnStateChanged += () => Interlocked.Increment(ref harness._dashboardNotifications);
            queue.OnEnqueue = _ => Interlocked.Increment(ref harness._tasksEnqueued);
            completionNotifier.OnTaskCompleted += _ =>
            {
                Interlocked.Increment(ref harness._transportNotifications);
                return Task.CompletedTask;
            };

            return harness;
        }

        /// <summary>
        /// THE SHARED LIFECYCLE: the body, then the STRICT teardown on every path, so no producer is
        /// ever leaked and a cleanup failure can never replace the assertion the reviewer needs.
        /// </summary>
        /// <param name="harness">The harness whose teardown must run.</param>
        /// <param name="body">The vector's assertions.</param>
        public static async Task RunAsync(AckHarness harness, Func<Task> body)
        {
            ExceptionDispatchInfo? primary = null;
            try
            {
                await body();
            }
            catch (Exception ex)
            {
                primary = ExceptionDispatchInfo.Capture(ex);
            }

            var cleanupFailure = await harness.StopAsync();

            primary?.Throw();

            if (cleanupFailure is not null)
                throw cleanupFailure;

            Assert.True(harness.StreamEnded, "the WorkStream producer is still live after teardown");
        }

        /// <summary>
        /// Registers the pinned worker's assignment context through the REAL insert-once store — the
        /// stored context the recorder's agreement rule requires.
        /// </summary>
        /// <param name="taskId">The opaque task id the context is recorded for.</param>
        /// <param name="workerId">The recorded worker, defaulting to the pinned one.</param>
        /// <returns>The recorded context, for assertions.</returns>
        public WorkerAssignmentContext RecordContext(string taskId, string? workerId = null)
        {
            var context = new WorkerAssignmentContext(
                "goal-dup",
                workerId ?? Worker.Id,
                DomainWorkerRole.Coder,
                new WorkSlot(taskId, new WorkSlotPosition(1, GoalPhase.Coding, 1), 1),
                "assigned-model");

            var write = Stores.AssignmentStore.InsertOnce(context);
            Assert.Equal(WorkerAssignmentWriteStatus.Recorded, write.Status);
            return context;
        }

        /// <summary>
        /// Builds a task carrying this harness's shape.
        /// </summary>
        /// <param name="taskId">The task's identifier.</param>
        /// <param name="model">The task's assigned model.</param>
        /// <returns>The task.</returns>
        public WorkTask BuildTask(string taskId, string model) => new()
        {
            TaskId = taskId,
            GoalId = "goal-dup",
            GoalDescription = "duplicate re-acknowledgement goal",
            Prompt = "do the work",
            Role = DomainWorkerRole.Coder,
            Model = model,
            Repositories = [],
        };

        /// <summary>
        /// Gives the pinned worker GENUINE active ownership of a task and records its assignment
        /// context, through the production assignment path and the REAL stores.
        /// </summary>
        /// <param name="taskId">The task's identifier.</param>
        /// <param name="model">The task's assigned model.</param>
        public void AssignTask(string taskId, string model) =>
            AssignTaskTo(Worker, taskId, model);

        /// <summary>
        /// The same production assignment path for ANY registered instance, so a second live worker
        /// can own real work of its own on its own stream.
        /// </summary>
        /// <param name="worker">The instance that will own the task.</param>
        /// <param name="taskId">The task's identifier.</param>
        /// <param name="model">The task's assigned model.</param>
        public void AssignTaskTo(ConnectedWorker worker, string taskId, string model)
        {
            GrantReadiness(worker);

            var task = BuildTask(taskId, model);
            Queue.Enqueue(task);
            var dequeued = Queue.TryDequeue(DomainWorkerRole.Unspecified);
            Assert.NotNull(dequeued);
            Service.ApplyTaskAssignment(worker, dequeued!);
            RecordContext(taskId, worker.Id);

            Assert.True(worker.IsBusy);
            Assert.Equal(taskId, worker.CurrentTaskId);
        }

        /// <summary>
        /// THE READINESS the production delivery boundary requires before an instance may be
        /// (re-)assigned: an instance whose negotiated completion was released waits for its own
        /// accepted Ready, and the claim refuses it until then.
        /// </summary>
        /// <remarks>
        /// <para>
        /// IT IS THE HARNESS's OWN READY, not a state write: the Ready is driven through the REAL
        /// checked idle (<c>TryMarkIdleForReady</c>) exactly as the production <c>HandleWorkerReady</c>
        /// does — so a harness assignment after a released negotiated completion takes the same
        /// route the worker's next Ready would.
        /// </para>
        /// <para>
        /// A WORKER THAT IS NOT WAITING IS UNTOUCHED, so this cannot mask the wait: it is a no-op on
        /// an instance that is already selectable, which is why the vectors that assert the wait
        /// itself keep asserting it directly.
        /// </para>
        /// </remarks>
        /// <param name="worker">The instance whose readiness is being established.</param>
        /// <returns>The worker's own Ready outcome.</returns>
        public bool GrantReadiness(ConnectedWorker worker) =>
            Pool.TryGetWorkerSnapshot(worker.Id, out var observed)
            && ReferenceEquals(observed.Worker, worker)
            && Pool.TryMarkIdleForReady(observed, queueEntryAbsent: true);

        /// <summary>Builds a completion payload with the requested model presence.</summary>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <param name="model">The model value to carry, or <c>null</c> for ABSENT.</param>
        /// <param name="output">The output to carry, or <c>null</c> for the default.</param>
        /// <param name="status">The wire status to carry.</param>
        /// <returns>The completion payload.</returns>
        public static GrpcTaskComplete BuildDuplicate(
            string taskId,
            string? model,
            string? output,
            CopilotHive.Shared.Grpc.TaskStatus status = CopilotHive.Shared.Grpc.TaskStatus.Completed)
        {
            var complete = new GrpcTaskComplete
            {
                TaskId = taskId,
                Status = status,
                Output = output ?? $"output-{taskId}",
            };

            if (model is not null)
                complete.Model = model;

            Assert.Equal(model is not null, complete.HasModel);
            return complete;
        }

        // ── THE ORDINARY COMPLETION (the setup every duplicate vector needs) ────────────────

        /// <summary>
        /// Runs ONE ordinary enabled completion: it is accepted, released, recorded and acknowledged,
        /// and it populates the single latest-eligible slot.
        /// </summary>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <returns>A task that completes once the handler has RETURNED.</returns>
        public async Task CompleteOrdinaryAsync(string taskId)
        {
            AssignTask(taskId, "assigned-model");

            var accepted = ServiceLogger.WaitFor(ProductionLogFragments.CompletionAccepted);
            var acknowledged = Writer.WaitForMessage(
                m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck);

            Reader.Push(new WorkerMessage
            {
                WorkerId = Worker.Id,
                Complete = BuildDuplicate(taskId, "assigned-model", null),
            });

            await accepted.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await acknowledged.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierAsync();
        }

        /// <summary>
        /// The DISABLED-registration counterpart: the completion is accepted and released as usual, but
        /// NO acknowledgement is awaited — a disabled registration publishes none, so waiting for one
        /// would hang forever. That absence is asserted by the caller.
        /// </summary>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <returns>A task that completes once the handler has RETURNED.</returns>
        public async Task CompleteOrdinaryWithoutAckAsync(string taskId)
        {
            AssignTask(taskId, "assigned-model");

            var accepted = ServiceLogger.WaitFor(ProductionLogFragments.CompletionAccepted);

            Reader.Push(new WorkerMessage
            {
                WorkerId = Worker.Id,
                Complete = BuildDuplicate(taskId, "assigned-model", null),
            });

            await accepted.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierAsync();

            // THE RELEASE REALLY HAPPENED — so this is an accepted completion, not a refusal.
            Assert.False(Worker.IsBusy);
            Assert.Null(Queue.GetActiveTask(taskId));
        }

        /// <summary>
        /// A DIRECT ordinary completion for the vectors that invoke a handler themselves and therefore
        /// own their own barrier. It returns once the handler has provably finished AND the pump has
        /// forwarded the ordinary acknowledgement, so a following count starts from a settled ledger.
        /// </summary>
        /// <remarks>
        /// THE PUMP IS PINNED FIRST. The production pump only binds the worker's channel once the
        /// stream has pinned the instance on its first inbound message, so a direct invocation with no
        /// prior stream message would have its acknowledgement queued and never forwarded. Pinning with
        /// a Progress message first is what makes the following wait for the forwarded acknowledgement
        /// meaningful.
        /// </remarks>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <param name="ackState">
        /// The TEST-OWNED eligibility holder this direct call advances. It is explicitly NOT a live
        /// RPC's local: production's holder belongs to one <c>WorkStream</c> invocation, and a direct
        /// handler call is not that invocation — so the caller owns this one and must pass the SAME
        /// instance to the duplicate that follows.
        /// </param>
        /// <returns>A task that completes once the handler finished and the ack was forwarded.</returns>
        public async Task CompleteOrdinaryDirectAsync(
            string taskId, WorkStreamCompletionAckState ackState)
        {
            await PinPumpAsync();

            AssignTask(taskId, "assigned-model");

            var method = typeof(HiveOrchestratorService).GetMethod(
                "HandleTaskComplete",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            var forwarded = Writer.WaitForMessage(
                m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck);

            try
            {
                method!.Invoke(
                    Service, [Worker, BuildDuplicate(taskId, "assigned-model", null), ackState]);
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }

            // THE ORDINARY PATH REALLY COMPLETED: released, recorded and acknowledged, with the
            // CALLER'S OWN holder naming this task and the acknowledgement already at the writer.
            await forwarded.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            Assert.False(Worker.IsBusy);
            Assert.NotNull(Stores.ReceiptStore.Load(taskId));
            Assert.Equal(taskId, ackState.LatestEligibleTaskId);
        }

        /// <summary>
        /// Pins the pump to the worker by pushing the stream's FIRST inbound message, so the production
        /// pump starts draining the worker's channel.
        /// </summary>
        /// <returns>A task that completes once the pinning Progress message was handled.</returns>
        public Task PinPumpAsync() => BarrierAsync();

        /// <summary>
        /// THE PUMP OBSERVATION BARRIER: proves the production pump is live by forwarding a uniquely
        /// tokened probe through the pinned instance's own channel, then asserts the writer carries no
        /// acknowledgement BEYOND the exact per-task counts the caller states.
        /// </summary>
        /// <remarks>
        /// THE CHANNEL IS FIFO and the production pump is its only consumer, so the probe can only be
        /// forwarded after anything enqueued BEFORE it — which means an acknowledgement queued by the
        /// handler under test was already observed at the writer by the time this returns. A refusal's
        /// absence claim is therefore made against a pump that provably drained, never against a
        /// possibly-stalled one. Callers state the acknowledgement count they already expect, so the
        /// probe neither weakens nor duplicates the ledger assertion.
        /// </remarks>
        /// <param name="expectedAcknowledgementsByTask">
        /// The exact per-task acknowledgement counts the ledger must report after the probe: each
        /// (taskId, count) pair is asserted exactly.
        /// </param>
        /// <returns>A task that completes once the pump provably forwarded the probe.</returns>
        public async Task AssertNoNewAcknowledgementThroughALivePumpAsync(
            params (string TaskId, int Count)[] expectedAcknowledgementsByTask)
        {
            var token = $"ack-pump-live-{Interlocked.Increment(ref _barrierSequence)}";
            var forwarded = Writer.WaitForMessage(m => m.UpdateAgents?.Role == token);

            Assert.True(
                Worker.MessageChannel.Writer.TryWrite(new OrchestratorMessage
                {
                    UpdateAgents = new UpdateAgents { Role = token },
                }),
                "the pump probe could not be queued; the pump observation would not be live.");

            var observed = await forwarded.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            Assert.Equal(token, observed.UpdateAgents.Role);

            foreach (var (taskId, count) in expectedAcknowledgementsByTask)
                Assert.Equal(count, AcknowledgedCount(taskId));
        }

        /// <summary>
        /// A POST-HANDLER BARRIER THAT DOES NOT TOUCH THE POOL: two Ready messages are pushed and each
        /// one's own production refusal line is awaited, so the SECOND arrival proves the message before
        /// it was fully handled.
        /// </summary>
        /// <remarks>
        /// WHY NOT <see cref="BarrierAsync"/>. The Progress barrier legitimately records task activity,
        /// so using it while proving that a duplicate does NOT refresh the successor's activity clock
        /// would make the proof impossible to interpret: the barrier would move the very timestamp under
        /// assertion. A Ready for a worker whose observed task still has an active queue entry is
        /// refused EARLY — before any idle, dequeue or activity write — and its refusal line is emitted
        /// on that terminal guard with no await after it, so it is a genuine post-handler barrier that
        /// mutates nothing.
        /// </remarks>
        /// <returns>A task that completes once both Ready refusals were observed.</returns>
        public async Task ReadyBarrierAsync()
        {
            await AwaitReadyRefusalAsync();
            await AwaitReadyRefusalAsync();
        }

        private async Task AwaitReadyRefusalAsync()
        {
            var ignored = ServiceLogger.WaitFor(
                HiveOrchestratorService.OwnershipRefusalReasons.ReadyTaskStillActive);

            Reader.Push(new WorkerMessage { WorkerId = Worker.Id, Ready = new WorkerReady() });

            await ignored.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Gives the pinned worker a SUCCESSOR task that is genuinely busy — the ordinary shape a
        /// duplicate delivery arrives in, and the state every "was anything disturbed" assertion
        /// compares against.
        /// </summary>
        /// <param name="taskId">The successor task's identifier.</param>
        /// <param name="model">The successor's model.</param>
        public void AssignSuccessor(string taskId, string model)
        {
            var task = BuildTask(taskId, model);
            Queue.Activate(task, Worker.Id);
            Pool.MarkBusy(Worker.Id, taskId);
            Worker.CurrentModel = model;

            Assert.True(Worker.IsBusy);
            Assert.Equal(taskId, Worker.CurrentTaskId);
        }

        /// <summary>
        /// AN IMMUTABLE FINGERPRINT of the successor's ownership: whether it is busy, which task it
        /// holds, its model, the task's presence in the queue, and BOTH activity timestamps.
        /// </summary>
        /// <remarks>
        /// THE TIMESTAMPS ARE THE ACTIVITY PROOF. A duplicate must not call <c>TouchActivity</c> for
        /// an old task, and it must not touch the successor's either, so capturing
        /// <see cref="ConnectedWorker.LastActivityAt"/> and
        /// <see cref="ConnectedWorker.CurrentTaskStartedAt"/> here is what makes "no activity was
        /// refreshed" an assertion rather than a claim.
        /// </remarks>
        /// <returns>A value-comparable snapshot of the observed state.</returns>
        public SuccessorObservation SuccessorSnapshot() => new(
            IsBusy: Worker.IsBusy,
            CurrentTaskId: Worker.CurrentTaskId,
            CurrentModel: Worker.CurrentModel,
            CurrentTaskStartedAt: Worker.CurrentTaskStartedAt,
            LastActivityAt: Worker.LastActivityAt,
            Role: Worker.Role,
            ContextUsagePercent: Worker.ContextUsagePercent);

        /// <summary>
        /// RE-ACTIVATES the stream's latest eligible task and makes the pooled worker busy with it
        /// again — the genuinely re-dispatched shape, live in both authorities.
        /// </summary>
        /// <param name="taskId">The latest eligible task's identifier.</param>
        public void ReactivateLatestTask(string taskId)
        {
            Queue.Activate(BuildTask(taskId, "assigned-model"), Worker.Id);
            Pool.MarkBusy(Worker.Id, taskId);
            Worker.CurrentModel = "assigned-model";

            Assert.True(Worker.IsBusy);
            Assert.Equal(taskId, Worker.CurrentTaskId);
            Assert.NotNull(Queue.GetActiveTask(taskId));
        }

        /// <summary>
        /// Pushes the pinned worker's activity clock into the past and returns that instant, so a
        /// LATER refresh by the production read loop is observable rather than assumed.
        /// </summary>
        /// <remarks>
        /// THE WRITE IS THE SAME FIELD PRODUCTION REFRESHES (<see cref="ConnectedWorker.LastActivityAt"/>,
        /// the value <c>WorkerPool.TouchActivity</c> sets and inactivity-based reclamation reads), so
        /// "was the refresh withheld?" becomes a strict comparison against a known-older instant.
        /// </remarks>
        /// <returns>The stale instant the clock was set to.</returns>
        public DateTime PushActivityClockBack()
        {
            var stale = DateTime.UtcNow.AddHours(-1);
            Worker.LastActivityAt = stale;
            return stale;
        }

        /// <summary>
        /// Delivers a completion for a task the worker GENUINELY HOLDS, through the REAL read loop,
        /// awaits its ORDINARY acceptance, and returns the pinned worker's activity clock AS IT STOOD
        /// AT THAT MOMENT.
        /// </summary>
        /// <remarks>
        /// <para>
        /// IT IS DELIBERATELY NOT A DUPLICATE HELPER. The point of the vector that uses it is that a
        /// held task's completion takes the ORDINARY path, so the signal awaited here is the ordinary
        /// path's own — a delivery that got routed to the read-only branch would never emit it and the
        /// wait would expire.
        /// </para>
        /// <para>
        /// THE ACTIVITY VALUE IS CAPTURED BEFORE THE BARRIER, AND THAT IS ESSENTIAL. The post-handler
        /// barrier sends a Progress message, and Progress legitimately calls <c>TouchActivity</c> — so a
        /// timestamp read AFTER the barrier would always look refreshed and could never detect a
        /// withheld refresh. The read here happens once the acceptance provenance line has been
        /// observed, which the handler emits after its validation gates and therefore strictly after
        /// the read loop already decided whether to refresh, so the captured value is exactly the
        /// loop's own decision.
        /// </para>
        /// </remarks>
        /// <param name="taskId">The held task's identifier.</param>
        /// <returns>The worker's activity instant as of the accepted completion, before any barrier.</returns>
        public async Task<DateTime> CompleteHeldTaskOrdinarilyAsync(string taskId)
        {
            // THE STORED ASSIGNMENT CONTEXT the recorder's agreement rule requires. The re-dispatch
            // reuses the same opaque task id, and the context store is insert-once, so the context this
            // task already has is the one the recorder will load — nothing new is recorded here.
            Assert.NotNull(Stores.AssignmentStore.Load(taskId));

            var accepted = ServiceLogger.WaitFor(ProductionLogFragments.CompletionAccepted);
            var acknowledged = Writer.WaitForMessage(
                m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck);

            Reader.Push(new WorkerMessage
            {
                WorkerId = Worker.Id,
                Complete = BuildDuplicate(taskId, "assigned-model", null),
            });

            await accepted.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            // CAPTURED HERE — after the loop's refresh decision, before any barrier can mask it.
            var activityAtAcceptance = Worker.LastActivityAt;

            await acknowledged.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierAsync();

            return activityAtAcceptance;
        }

        /// <summary>
        /// Replaces the pinned instance under the same worker id through the REAL registration RPC,
        /// with the SAME negotiated enablement — the ABA replacement a duplicate must not be answered
        /// for.
        /// </summary>
        /// <returns>The replacement instance.</returns>
        public ConnectedWorker RegisterReplacementInstance()
        {
            var response = Service.Register(
                new RegisterRequest
                {
                    WorkerId = Worker.Id,
                    RequestCompletionReceiptAck = true,
                },
                MockContext()).GetAwaiter().GetResult();

            Assert.True(response.Accepted);
            Assert.True(response.CompletionReceiptAckEnabled);

            return Pool.GetWorker(Worker.Id)
                ?? throw new InvalidOperationException(
                    $"the replacement registration did not publish '{Worker.Id}'.");
        }

        /// <summary>
        /// The instance-aware replacement used by <see cref="ReplacePinnedInstance"/>: the old
        /// instance is removed first, exactly as a stream teardown would, so the replacement really is
        /// a NEW object with FRESH state.
        /// </summary>
        /// <returns>The replacement instance.</returns>
        public ConnectedWorker ReplacePinnedInstance()
        {
            Assert.True(Pool.RemoveWorker(Worker));
            return RegisterReplacementInstance();
        }

        // ── THE DUPLICATE DELIVERY, ON THE REAL STREAM ──────────────────────────────────────

        /// <summary>
        /// Pushes a duplicate that the branch must RE-ACKNOWLEDGE, awaits the production success line
        /// and the pump's own forwarding, then the post-handler barrier.
        /// </summary>
        /// <param name="taskId">The duplicated task's identifier.</param>
        /// <param name="model">The model the duplicate carries (PRESENT).</param>
        /// <returns>A task that completes once the acknowledgement was observed and the handler returned.</returns>
        public async Task DuplicateAndAwaitReAckAsync(string taskId, string model)
        {
            var acknowledged = ServiceLogger.WaitFor(ProductionLogFragments.DuplicateReAcknowledged);
            var forwarded = Writer.WaitForMessage(
                m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck);

            Reader.Push(new WorkerMessage
            {
                WorkerId = Worker.Id,
                Complete = BuildDuplicate(taskId, model, null),
            });

            await acknowledged.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            // THE PUBLICATION IS OBSERVED AT THE WRITER the pump forwards to.
            var message = await forwarded.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            Assert.Equal(taskId, message.CompletionReceiptAck.TaskId);
            Assert.Equal(Worker.Id, message.CompletionReceiptAck.WorkerId);

            await ReadyBarrierAsync();
        }

        /// <summary>
        /// Pushes a duplicate and waits ONLY on the POST-HANDLER BARRIER, for vectors whose outcome the
        /// successful-publication line cannot describe (a refused enqueue publishes no success line).
        /// </summary>
        /// <param name="taskId">The duplicated task's identifier.</param>
        /// <param name="model">The model the duplicate carries (PRESENT).</param>
        /// <returns>A task that completes once the handler provably returned.</returns>
        public async Task DuplicateAndBarrierAsync(string taskId, string model)
        {
            Reader.Push(new WorkerMessage
            {
                WorkerId = Worker.Id,
                Complete = BuildDuplicate(taskId, model, null),
            });

            await ReadyBarrierAsync();
        }

        /// <summary>
        /// Pushes a duplicate that the branch must REFUSE for an ARBITRARY reason, awaits the guarded
        /// duplicate diagnostic, then the post-handler barrier. It asserts the diagnostic named the
        /// expected reason, the task and the worker, so the refusal is attributed rather than assumed.
        /// </summary>
        /// <param name="taskId">The duplicated task's identifier.</param>
        /// <param name="model">The model the duplicate carries, or <c>null</c> for ABSENT.</param>
        /// <param name="output">The output to carry, or <c>null</c> for the default.</param>
        /// <param name="expectedReason">
        /// The exact guard reason the diagnostic must name, or <c>null</c> to assert only that SOME
        /// duplicate refusal was reported for this task.
        /// </param>
        /// <param name="expectedAcknowledgementsForTask">
        /// The acknowledgement count the writer must still report for this task after the pump
        /// observation probe — the ordinary completion's own by default.
        /// </param>
        /// <returns>A task that completes once the refusal was observed and the handler returned.</returns>
        public async Task DuplicateAndAwaitRefusalAsync(
            string taskId,
            string? model,
            string? output,
            string? expectedReason = null,
            int expectedAcknowledgementsForTask = 1)
        {
            var ignored = ServiceLogger.WaitFor(ProductionLogFragments.DuplicateIgnored);
            Reader.Push(new WorkerMessage
            {
                WorkerId = Worker.Id,
                Complete = BuildDuplicate(taskId, model, output),
            });
            await ignored.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await ReadyBarrierAsync();

            Assert.Contains(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.DuplicateIgnored, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal)
                     && m.Contains(Worker.Id, StringComparison.Ordinal)
                     && (expectedReason is null
                         || m.Contains(expectedReason, StringComparison.Ordinal)));

            // THE LEDGER IS RE-READ THROUGH A LIVE PUMP: the probe proves the channel was drained past
            // anything the handler could have queued, so a non-publication is a refusal, not a stall.
            await AssertNoNewAcknowledgementThroughALivePumpAsync((taskId, expectedAcknowledgementsForTask));
        }

        /// <summary>
        /// Pushes a duplicate whose MAPPING must fail, awaits the production mapping-failure warning and
        /// then the post-handler barrier — and separately asserts the duplicate's own guarded refusal
        /// never fired, so the refusal really is the mapping.
        /// </summary>
        /// <param name="taskId">The duplicated task's identifier.</param>
        /// <param name="model">The model the duplicate carries (PRESENT).</param>
        /// <param name="status">The wire status the mapper refuses.</param>
        /// <returns>A task that completes once the refusal was observed and the handler returned.</returns>
        public async Task DuplicateAndAwaitMappingRefusalAsync(
            string taskId, string model, CopilotHive.Shared.Grpc.TaskStatus status)
        {
            var mapped = ServiceLogger.WaitFor(ProductionLogFragments.MappingFailed);
            Reader.Push(new WorkerMessage
            {
                WorkerId = Worker.Id,
                Complete = BuildDuplicate(taskId, model, null, status),
            });
            await mapped.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await ReadyBarrierAsync();

            Assert.DoesNotContain(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.DuplicateIgnored, StringComparison.Ordinal));

            // THE LEDGER IS RE-READ THROUGH A LIVE PUMP: nothing was acknowledged, observed after the
            // channel was provably drained.
            await AssertNoNewAcknowledgementThroughALivePumpAsync((taskId, 1));
        }

        /// <summary>
        /// Pushes an ORDINARY completion that must be refused by an existing ownership guard, awaits
        /// that guard's own warning (so the refusal is attributed), then the post-handler barrier.
        /// </summary>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <param name="expectedReason">The refusing guard's reason text.</param>
        /// <returns>A task that completes once the refusal was observed and the handler returned.</returns>
        public async Task CompleteAndAwaitOrdinaryRefusalAsync(string taskId, string expectedReason)
        {
            var signal = ServiceLogger.WaitFor(expectedReason);
            Reader.Push(new WorkerMessage
            {
                WorkerId = Worker.Id,
                Complete = BuildDuplicate(taskId, "assigned-model", null),
            });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierAsync();

            Assert.Contains(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionIgnored, StringComparison.Ordinal)
                     && m.Contains(expectedReason, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));
        }

        /// <summary>
        /// Invokes the duplicate branch SYNCHRONOUSLY with a supplied pinned instance, for the ABA
        /// vectors whose replacement would otherwise end the real read loop before a barrier could run.
        /// </summary>
        /// <param name="pinned">The pinned instance to hand the handler.</param>
        /// <param name="taskId">The duplicated task's identifier.</param>
        /// <param name="model">The model the duplicate carries, or <c>null</c> for ABSENT.</param>
        /// <param name="output">The output to carry, or <c>null</c> for the default.</param>
        /// <param name="ackState">
        /// The TEST-OWNED eligibility holder this direct call is routed against — the SAME instance
        /// the simulated ordinary completion advanced, when the vector simulates that sequence.
        /// </param>
        public void InvokeDuplicateDirectly(
            ConnectedWorker pinned,
            string taskId,
            string? model,
            string? output,
            WorkStreamCompletionAckState ackState)
        {
            var method = typeof(HiveOrchestratorService).GetMethod(
                "HandleTaskComplete",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            try
            {
                method!.Invoke(Service, [pinned, BuildDuplicate(taskId, model, output), ackState]);
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        // ── THE ONE-CLASSIFICATION SEAM ──────────────────────────────────────────────────────

        // ── THE REAL PRODUCTION BOUNDARY ─────────────────────────────────────────────────────

        /// <summary>
        /// What ONE delivery that travelled the REAL <c>WorkStream</c> Complete arm did, observed at
        /// the production boundary rather than by replaying the arm's steps.
        /// </summary>
        /// <param name="ActivityRefreshed">
        /// Whether the arm refreshed the worker's activity clock for this delivery — read across the
        /// production window itself, so it reflects the arm's own decision and nothing later.
        /// </param>
        /// <param name="MutationRan">Whether the injected window mutation actually fired.</param>
        public sealed record BoundaryDelivery(bool ActivityRefreshed, bool MutationRan);

        /// <summary>
        /// Pushes ONE completion through the REAL <c>WorkStream</c> read loop and runs
        /// <paramref name="mutation"/> INSIDE the production window — after the arm's activity decision
        /// and before it invokes the handler — then returns what the arm itself did.
        /// </summary>
        /// <remarks>
        /// <para>
        /// THIS IS THE PRODUCTION BOUNDARY, NOT A REPLAY OF IT. The message is written to the stream's
        /// own request reader, so the arm's classification, its activity decision and its handler call
        /// are all production code executing in their real order. The only test-owned thing in the path
        /// is the window hook, which observes and mutates but decides nothing — so if the arm ever
        /// stops carrying the single routing value, the vectors built on this helper break.
        /// </para>
        /// <para>
        /// THE ACTIVITY OUTCOME IS CAPTURED IN THE WINDOW, which is the only place it can be read
        /// honestly: the post-handler barrier is a Progress message and Progress legitimately refreshes
        /// activity, so a later read would always look refreshed. The hook therefore records the clock
        /// as the arm left it, before the handler and before any barrier.
        /// </para>
        /// <para>
        /// THE MUTATION IS DETERMINISTIC because the read loop is synchronous through the arm: the hook
        /// runs on the loop's own thread, strictly between the two statements under test, with this
        /// very message in flight. There is no sleep and no second consumer of the stream.
        /// </para>
        /// </remarks>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <param name="model">The model the delivery carries, or <c>null</c> for ABSENT.</param>
        /// <param name="mutation">The ownership change to apply inside the window.</param>
        /// <returns>The arm's own activity outcome, once the handler has provably returned.</returns>
        public async Task<BoundaryDelivery> DeliverThroughWorkStreamWithWindowMutationAsync(
            string taskId, string? model, Action mutation)
        {
            var hookField = typeof(HiveOrchestratorService).GetField(
                "_afterCompletionActivityDecisionForTest",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(hookField);

            var activityBefore = Worker.LastActivityAt;
            var mutationRan = false;
            var activityRefreshedInWindow = false;
            Exception? hookFailure = null;

            // THE WINDOW HOOK, armed for exactly THIS delivery and disarmed as it fires, so it can
            // never affect a later message on the same stream.
            Action<ConnectedWorker, string> hook = null!;
            hook = (pinned, deliveredTaskId) =>
            {
                hookField!.SetValue(Service, null);

                try
                {
                    // The arm's OWN activity decision, read before the handler runs.
                    activityRefreshedInWindow = pinned.LastActivityAt > activityBefore;

                    Assert.Same(Worker, pinned);
                    Assert.Equal(taskId, deliveredTaskId);

                    mutation();
                    mutationRan = true;
                }
                catch (Exception ex)
                {
                    // CAPTURED, NEVER THROWN INTO PRODUCTION: a hook that faulted the stream would
                    // destroy the evidence instead of reporting it. The vector rethrows below.
                    hookFailure = ex;
                }
            };

            hookField!.SetValue(Service, hook);

            try
            {
                Reader.Push(new WorkerMessage
                {
                    WorkerId = Worker.Id,
                    Complete = BuildDuplicate(taskId, model, null),
                });

                // THE POST-HANDLER BARRIER proves the arm — classification, activity decision, window
                // and handler — fully returned for this message.
                //
                // THE PROGRESS BARRIER IS SAFE HERE, even though Progress refreshes activity: this
                // helper captured the arm's activity outcome INSIDE the window, before the handler and
                // long before any barrier, so a later refresh cannot contaminate that evidence. The
                // activity-neutral Ready barrier is deliberately NOT used, because a vector whose
                // mutation releases the worker leaves no held task for Ready to be refused over.
                await BarrierAsync();
            }
            finally
            {
                hookField.SetValue(Service, null);
            }

            if (hookFailure is not null)
                ExceptionDispatchInfo.Capture(hookFailure).Throw();

            Assert.True(mutationRan, "the production window hook never fired for this delivery");
            return new BoundaryDelivery(activityRefreshedInWindow, mutationRan);
        }

        /// <summary>
        /// Releases the pinned worker's hold on a task and removes its active queue entry — the
        /// "became unheld" mutation.
        /// </summary>
        /// <param name="taskId">The task to release.</param>
        public void ReleaseTask(string taskId)
        {
            Pool.MarkIdle(Worker.Id);
            Queue.MarkComplete(taskId);

            Assert.False(Worker.IsBusy);
            Assert.Null(Queue.GetActiveTask(taskId));
        }

        // ── LOSING STREAMS AND THE BARRIER ──────────────────────────────────────────────────

        /// <summary>
        /// Starts a SECOND stream for the same instance whose FIRST message is the supplied one, awaits
        /// its termination and classifies how it ended, so a loser's clean return is asserted rather
        /// than assumed. The stream is retained and joined by the same strict teardown.
        /// </summary>
        /// <param name="workerId">The worker id the first message names.</param>
        /// <param name="firstMessage">The message the second stream sees first.</param>
        /// <returns>The retained handle, already finished.</returns>
        public async Task<SecondStream> StartSecondStreamAndAwaitTerminationAsync(
            string workerId, WorkerMessage firstMessage)
        {
            var reader = new ChannelStreamReader();
            var writer = new DuplicateObservingWriter();
            var producer = Service.WorkStream(reader, writer, MockContext());

            var handle = new SecondStream(reader, writer, producer, workerId);
            _extraStreams.Add(handle);

            reader.Push(firstMessage);

            try
            {
                await producer.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
                handle.Completion = new StreamCompletion(Faulted: false, Fault: null);
            }
            catch (Exception ex)
            {
                // A clean loser must RETURN, never fault.
                handle.Completion = new StreamCompletion(Faulted: true, Fault: ex);
            }

            return handle;
        }

        // ── THE LIVE ELIGIBILITY PROBES ─────────────────────────────────────────────────────

        /// <summary>
        /// THE LIVE PROOF THAT THIS STREAM'S OWN LATEST ELIGIBILITY STILL NAMES
        /// <paramref name="taskId"/>: another identical duplicate is delivered through the REAL read
        /// loop and must be RE-ACKNOWLEDGED, which ONLY the stream's retained latest id can authorize.
        /// </summary>
        /// <remarks>
        /// IT IS A BEHAVIOURAL PROBE, NOT A STATE READ. The eligibility holder belongs to one
        /// <c>WorkStream</c> invocation and is deliberately unreachable from here, so the property is
        /// asserted the only honest way: by exercising the protocol the eligibility authorizes. A
        /// regression that cleared, failed to advance or relocated the holder produces an ORDINARY
        /// refusal here and the wait for the success line expires as a named failure.
        /// </remarks>
        /// <param name="taskId">The task the stream's latest eligibility must still name.</param>
        /// <param name="expectedAcknowledgementsAfter">
        /// The cumulative acknowledgement count for that task once this probe's own
        /// re-acknowledgement has been forwarded.
        /// </param>
        /// <returns>A task that completes once the re-acknowledgement was observed at the writer.</returns>
        public async Task AssertLatestEligibleStillReAcknowledgedAsync(
            string taskId, int expectedAcknowledgementsAfter)
        {
            var acknowledged = ServiceLogger.WaitFor(ProductionLogFragments.DuplicateReAcknowledged);
            var forwarded = Writer.WaitForMessage(
                m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck
                     && string.Equals(
                         m.CompletionReceiptAck.TaskId, taskId, StringComparison.Ordinal));

            Reader.Push(new WorkerMessage
            {
                WorkerId = Worker.Id,
                Complete = BuildDuplicate(taskId, "assigned-model", null),
            });

            await acknowledged.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            var message = await forwarded.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            Assert.Equal(Worker.Id, message.CompletionReceiptAck.WorkerId);

            await BarrierAsync();
            Assert.Equal(expectedAcknowledgementsAfter, AcknowledgedCount(taskId));
        }

        /// <summary>
        /// The SAME live probe for a task that is currently HELD AGAIN: the hold is released through
        /// the pool and the queue FIRST — test-owned state, never a completion — because a held task
        /// can never be classified as a duplicate at all, and then the probe runs unchanged.
        /// </summary>
        /// <param name="taskId">The task the stream's latest eligibility must still name.</param>
        /// <param name="expectedAcknowledgementsAfter">The cumulative count after the probe.</param>
        /// <returns>A task that completes once the re-acknowledgement was observed at the writer.</returns>
        public async Task AssertLatestEligibleSurvivedAndReAcknowledgesAfterReleaseAsync(
            string taskId, int expectedAcknowledgementsAfter)
        {
            ReleaseTask(taskId);
            await AssertLatestEligibleStillReAcknowledgedAsync(taskId, expectedAcknowledgementsAfter);
        }

        /// <summary>
        /// THE LIVE PROOF FOR A STREAM WHOSE CHANNEL CAN NO LONGER CARRY AN ACKNOWLEDGEMENT: the next
        /// identical duplicate still ENTERS the read-only branch and performs its REAL confirmation
        /// read, and is refused only at the enqueue.
        /// </summary>
        /// <remarks>
        /// THE DISCRIMINATOR IS THE ROUTING. A stream whose eligibility had been cleared by the failed
        /// enqueue would send this delivery down the ORDINARY path, where an idle-for-that-task worker
        /// is refused by the busy gate — so the absence of that ordinary refusal, together with a REAL
        /// additional confirmation read, is what proves the eligibility survived.
        /// </remarks>
        /// <param name="taskId">The task the stream's latest eligibility must still name.</param>
        /// <returns>A task that completes once the refused enqueue was observed and the handler returned.</returns>
        public async Task AssertLatestEligibleStillReachesConfirmationDespiteLostEnqueueAsync(string taskId)
        {
            var confirmationsBefore = Recorder.ConfirmCalls;
            var notQueuedBefore = CountNotQueuedDiagnostics(taskId);

            var notQueued = ServiceLogger.WaitFor(ProductionLogFragments.ReceiptAckNotQueued);

            Reader.Push(new WorkerMessage
            {
                WorkerId = Worker.Id,
                Complete = BuildDuplicate(taskId, "assigned-model", null),
            });

            await notQueued.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierAsync();

            // THE READ-ONLY BRANCH REALLY RAN: one more genuine confirmation read, and one more
            // guarded lost-enqueue diagnostic for this exact task.
            Assert.Equal(confirmationsBefore + 1, Recorder.ConfirmCalls);
            Assert.Equal(notQueuedBefore + 1, CountNotQueuedDiagnostics(taskId));

            // …AND THE ORDINARY PATH WAS NEVER TAKEN for it, which is where a cleared eligibility
            // would have sent it.
            Assert.DoesNotContain(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionIgnored, StringComparison.Ordinal)
                     && m.Contains(
                         HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask,
                         StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));
        }

        private int CountNotQueuedDiagnostics(string taskId) =>
            ServiceLogger.Messages.Count(
                m => m.Contains(ProductionLogFragments.ReceiptAckNotQueued, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));

        // ── ADDITIONAL LIVE STREAMS AND LIVE WORKERS ────────────────────────────────────────

        /// <summary>
        /// Registers ANOTHER worker through the REAL registration RPC with the acknowledgement
        /// NEGOTIATED, so a second genuinely enabled instance exists on the SAME service.
        /// </summary>
        /// <param name="workerId">The additional worker's id.</param>
        /// <returns>The published instance.</returns>
        public ConnectedWorker RegisterAdditionalWorker(string workerId)
        {
            var response = Service.Register(
                new RegisterRequest { WorkerId = workerId, RequestCompletionReceiptAck = true },
                MockContext()).GetAwaiter().GetResult();

            Assert.True(response.Accepted);
            Assert.True(response.CompletionReceiptAckEnabled);

            var worker = Pool.GetWorker(workerId)
                ?? throw new InvalidOperationException(
                    $"the registration RPC did not publish '{workerId}'.");
            Assert.True(worker.CompletionReceiptAckEnabled);
            return worker;
        }

        /// <summary>
        /// Opens ANOTHER REAL <c>WorkStream</c> on the SAME service, with its own request reader and
        /// its own response writer, and retains it for the strict teardown.
        /// </summary>
        /// <remarks>
        /// IT IS A GENUINE PRODUCTION INVOCATION, which is the whole point: the acknowledgement
        /// eligibility it may build up belongs to THAT invocation, so anything this stream does — or
        /// refuses to do — is evidence about a real stream rather than about a fixture's own state.
        /// </remarks>
        /// <param name="workerId">The worker id this stream's messages name.</param>
        /// <returns>The retained handle.</returns>
        public SecondStream StartStream(string workerId)
        {
            var reader = new ChannelStreamReader();
            var writer = new DuplicateObservingWriter();
            var producer = Service.WorkStream(reader, writer, MockContext());

            var handle = new SecondStream(reader, writer, producer, workerId);
            _extraStreams.Add(handle);
            return handle;
        }

        /// <summary>
        /// THE POST-HANDLER BARRIER FOR AN ADDITIONAL STREAM, identical in kind to
        /// <see cref="BarrierAsync"/>: its uniquely tokened Progress line can only be logged once
        /// that stream's previous handler RETURNED. Its first use also PINS the stream's instance.
        /// </summary>
        /// <param name="stream">The additional stream to advance.</param>
        /// <returns>A task that completes once that stream demonstrably advanced.</returns>
        public async Task BarrierOnAsync(SecondStream stream)
        {
            var token = $"ack-extra-barrier-{Interlocked.Increment(ref _barrierSequence)}";
            var signal = ServiceLogger.WaitFor(token);

            stream.Reader.Push(new WorkerMessage
            {
                WorkerId = stream.WorkerId,
                Progress = new TaskProgress
                {
                    TaskId = "barrier",
                    Status = CopilotHive.Shared.Grpc.TaskStatus.InProgress,
                    Message = token,
                },
            });

            try
            {
                await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    $"POST-HANDLER BARRIER '{token}' was never reached on the additional stream for " +
                    $"'{stream.WorkerId}': its read loop did not process the following message, so the " +
                    "handler under test did NOT return normally.",
                    ex);
            }
        }

        /// <summary>
        /// Runs ONE ordinary enabled completion ON AN ADDITIONAL LIVE STREAM, awaiting the
        /// acknowledgement AT THAT STREAM'S OWN WRITER and then that stream's post-handler barrier.
        /// </summary>
        /// <param name="stream">The live stream the completion travels.</param>
        /// <param name="worker">The instance that stream is pinned to.</param>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <returns>A task that completes once the handler returned and the ack was forwarded.</returns>
        public async Task CompleteOrdinaryOnAsync(
            SecondStream stream, ConnectedWorker worker, string taskId)
        {
            AssignTaskTo(worker, taskId, "assigned-model");

            var forwarded = stream.Writer.WaitForMessage(
                m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck
                     && string.Equals(
                         m.CompletionReceiptAck.TaskId, taskId, StringComparison.Ordinal));

            stream.Reader.Push(new WorkerMessage
            {
                WorkerId = worker.Id,
                Complete = BuildDuplicate(taskId, "assigned-model", null),
            });

            await forwarded.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierOnAsync(stream);

            Assert.False(worker.IsBusy);
            Assert.Null(Queue.GetActiveTask(taskId));
        }

        /// <summary>
        /// Delivers a duplicate ON AN ADDITIONAL LIVE STREAM that the branch must RE-ACKNOWLEDGE,
        /// observing the publication at THAT stream's own writer.
        /// </summary>
        /// <param name="stream">The live stream the duplicate travels.</param>
        /// <param name="worker">The instance that stream is pinned to.</param>
        /// <param name="taskId">The duplicated task's identifier.</param>
        /// <returns>A task that completes once the re-acknowledgement was observed.</returns>
        public async Task DuplicateAndAwaitReAckOnAsync(
            SecondStream stream, ConnectedWorker worker, string taskId)
        {
            var forwarded = stream.Writer.WaitForMessage(
                m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck
                     && string.Equals(
                         m.CompletionReceiptAck.TaskId, taskId, StringComparison.Ordinal));

            stream.Reader.Push(new WorkerMessage
            {
                WorkerId = worker.Id,
                Complete = BuildDuplicate(taskId, "assigned-model", null),
            });

            var message = await forwarded.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            Assert.Equal(worker.Id, message.CompletionReceiptAck.WorkerId);

            await BarrierOnAsync(stream);
        }

        /// <summary>
        /// Delivers a completion ON AN ADDITIONAL LIVE STREAM that the ORDINARY ownership guards must
        /// refuse, then that stream's post-handler barrier — the shape a delivery takes when the
        /// stream's OWN eligibility holder does not name the task.
        /// </summary>
        /// <param name="stream">The live stream the delivery travels.</param>
        /// <param name="worker">The instance that stream is pinned to.</param>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <param name="expectedReason">The ordinary guard reason the diagnostic must name.</param>
        /// <returns>A task that completes once the refusal was observed and the handler returned.</returns>
        public async Task CompleteAndAwaitOrdinaryRefusalOnAsync(
            SecondStream stream, ConnectedWorker worker, string taskId, string expectedReason)
        {
            var signal = ServiceLogger.WaitFor(expectedReason);

            stream.Reader.Push(new WorkerMessage
            {
                WorkerId = worker.Id,
                Complete = BuildDuplicate(taskId, "assigned-model", null),
            });

            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierOnAsync(stream);

            Assert.Contains(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionIgnored, StringComparison.Ordinal)
                     && m.Contains(expectedReason, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal)
                     && m.Contains(worker.Id, StringComparison.Ordinal));
        }

        /// <summary>
        /// THE PUMP OBSERVATION BARRIER FOR AN ADDITIONAL STREAM: proves that stream's production
        /// pump is live by forwarding a uniquely tokened probe through the instance's OWN channel,
        /// then asserts its writer carries no acknowledgement beyond the stated per-task counts.
        /// </summary>
        /// <param name="stream">The additional stream whose writer is observed.</param>
        /// <param name="worker">The instance that stream is pinned to.</param>
        /// <param name="expectedAcknowledgementsByTask">The exact per-task counts on that writer.</param>
        /// <returns>A task that completes once that pump provably forwarded the probe.</returns>
        public async Task AssertNoNewAcknowledgementThroughALivePumpOnAsync(
            SecondStream stream,
            ConnectedWorker worker,
            params (string TaskId, int Count)[] expectedAcknowledgementsByTask)
        {
            var token = $"ack-extra-pump-live-{Interlocked.Increment(ref _barrierSequence)}";
            var forwarded = stream.Writer.WaitForMessage(m => m.UpdateAgents?.Role == token);

            Assert.True(
                worker.MessageChannel.Writer.TryWrite(new OrchestratorMessage
                {
                    UpdateAgents = new UpdateAgents { Role = token },
                }),
                "the pump probe could not be queued; the pump observation would not be live.");

            var observed = await forwarded.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            Assert.Equal(token, observed.UpdateAgents.Role);

            foreach (var (taskId, count) in expectedAcknowledgementsByTask)
                Assert.Equal(count, AcknowledgedCountOn(stream, taskId));
        }

        /// <summary>
        /// How many DUPLICATE-BRANCH diagnostics of one kind name <paramref name="taskId"/> so far.
        /// </summary>
        /// <remarks>
        /// A COUNT, NOT A PRESENCE CHECK, because the logger's history is CUMULATIVE. A vector that
        /// legitimately exercised the duplicate branch earlier — for example to PROVE the stream's
        /// eligibility before the case under test — would make a bare "was it ever emitted" assertion
        /// permanently false. Comparing a baseline taken immediately before the delivery under test
        /// against the count after it keeps the claim about THAT delivery.
        /// </remarks>
        /// <param name="fragment">The production fragment to count.</param>
        /// <param name="taskId">The task the diagnostic must name.</param>
        /// <returns>The number of matching messages logged so far.</returns>
        public int DiagnosticCount(string fragment, string taskId) =>
            ServiceLogger.Messages.Count(
                m => m.Contains(fragment, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));

        /// <summary>How many acknowledgements a given stream's writer carries for a task id.</summary>
        /// <param name="stream">The stream whose writer is inspected.</param>
        /// <param name="taskId">The opaque task id the acknowledgement must name.</param>
        /// <returns>The count of forwarded acknowledgements for that exact id on that stream.</returns>
        public static int AcknowledgedCountOn(SecondStream stream, string taskId) =>
            stream.Writer.Acknowledgements().Count(
                a => string.Equals(a.TaskId, taskId, StringComparison.Ordinal));

        /// <summary>
        /// THE POST-HANDLER BARRIER: pushes a Progress message carrying a UNIQUE token and waits for
        /// <c>HandleTaskProgress</c>'s own production log line. Because the read loop is strictly
        /// sequential, that line can only be emitted once the PREVIOUS handler RETURNED — so a handler
        /// that threw never lets the loop reach it and the wait expires as a named failure.
        /// </summary>
        /// <returns>A task that completes once the loop demonstrably advanced.</returns>
        public async Task BarrierAsync()
        {
            var token = $"ack-barrier-{Interlocked.Increment(ref _barrierSequence)}";
            var signal = ServiceLogger.WaitFor(token);

            Reader.Push(new WorkerMessage
            {
                WorkerId = Worker.Id,
                Progress = new TaskProgress
                {
                    TaskId = "barrier",
                    Status = CopilotHive.Shared.Grpc.TaskStatus.InProgress,
                    Message = token,
                },
            });

            try
            {
                await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    $"POST-HANDLER BARRIER '{token}' was never reached: the WorkStream read loop did " +
                    "not process the following message, which means the handler under test did NOT " +
                    $"return normally (streamCompleted={StreamTask.IsCompleted}).",
                    ex);
            }
        }

        /// <summary>
        /// THE STRICT TEARDOWN: ends the request stream, joins the retained producer with a finite
        /// bound, and joins every additional stream. It NEVER throws — the outcome is RETURNED so a
        /// primary assertion failure stays authoritative.
        /// </summary>
        /// <returns><c>null</c> when everything terminated cleanly; otherwise the failure.</returns>
        public async Task<Exception?> StopAsync()
        {
            Reader.Complete();

            Exception? failure = null;
            try
            {
                await StreamTask.WaitAsync(BoundedWait, CancellationToken.None);
            }
            catch (TimeoutException ex)
            {
                failure = new TimeoutException(
                    "TEARDOWN LEAK: the WorkStream did not terminate within " +
                    $"{BoundedWait.TotalSeconds:F0}s — a live producer remains.",
                    ex);
            }
            catch (Exception ex) when (StreamTask.IsCompleted)
            {
                failure = new InvalidOperationException(
                    "THE WORKSTREAM TERMINATED WITH A FAULT. A clean vector must leave the transport " +
                    "draining normally; a fault here means a handler escaped instead of returning.",
                    ex);
            }
            catch (Exception ex)
            {
                failure = new InvalidOperationException(
                    "TEARDOWN LEAK: the WorkStream is still running after its join failed — a live " +
                    "producer remains.",
                    ex);
            }

            // ── EVERY EXTRA PRODUCER IS COMPLETED AND JOINED, UNCONDITIONALLY ────────────────
            // THE JOIN IS NEVER SKIPPED ON IsCompleted, and that is the whole point: a task is
            // "completed" the instant it FAULTS too, so an `if (!IsCompleted)` guard fires exactly
            // when a fault is already sitting there unobserved — silently exempting the one outcome
            // this strict teardown exists to surface. Awaiting unconditionally is also cheap for an
            // already-finished task, and it is what makes "every producer joined" literally true.
            foreach (var extra in _extraStreams)
            {
                extra.Reader.Complete();

                try
                {
                    await extra.Producer.WaitAsync(BoundedWait, CancellationToken.None);
                }
                catch (TimeoutException ex)
                {
                    failure ??= new TimeoutException(
                        $"TEARDOWN LEAK: the additional WorkStream for '{extra.WorkerId}' did not " +
                        $"terminate within {BoundedWait.TotalSeconds:F0}s — a live producer remains.",
                        ex);
                }
                catch (Exception ex) when (extra.Producer.IsCompleted)
                {
                    failure ??= new InvalidOperationException(
                        $"THE ADDITIONAL WORKSTREAM FOR '{extra.WorkerId}' TERMINATED WITH A FAULT. " +
                        "A clean vector must leave every transport draining normally; a fault here " +
                        "means a handler escaped instead of returning.",
                        ex);
                }
                catch (Exception ex)
                {
                    failure ??= new InvalidOperationException(
                        $"TEARDOWN LEAK: the additional WorkStream for '{extra.WorkerId}' is still " +
                        "running after its join failed — a live producer remains.",
                        ex);
                }
            }

            // ── THE SHARED STORES ARE DISPOSED LAST, AFTER EVERY PRODUCER HAS STOPPED ────────
            // Disposing them earlier would pull the database out from under a stream that is still
            // live, so a producer could observe store failures caused purely by teardown order —
            // and a retained producer would outlive the state it reads. Every join above has already
            // completed (or been recorded as a failure) by the time this runs.
            try
            {
                Stores.Dispose();
            }
            catch
            {
                // Best-effort — a leftover fixture must never fail a test, and a disposal problem
                // must never replace the assertion (or the join failure) the reviewer needs.
            }

            // THE FIRST FAILURE IS RETURNED, NEVER THROWN: RunAsync rethrows the vector's own
            // primary assertion first, so cleanup can never mask it.
            return failure;
        }

        /// <summary>AN IMMUTABLE FINGERPRINT of the successor's ownership and both activity timestamps.</summary>
        /// <param name="IsBusy">Whether the worker was busy.</param>
        /// <param name="CurrentTaskId">The task the worker held.</param>
        /// <param name="CurrentModel">The worker's current model.</param>
        /// <param name="CurrentTaskStartedAt">When the current task started.</param>
        /// <param name="LastActivityAt">The last task-specific activity instant.</param>
        /// <param name="Role">The worker's role.</param>
        /// <param name="ContextUsagePercent">The worker's reported context usage.</param>
        public sealed record SuccessorObservation(
            bool IsBusy,
            string? CurrentTaskId,
            string? CurrentModel,
            DateTime? CurrentTaskStartedAt,
            DateTime LastActivityAt,
            DomainWorkerRole Role,
            int ContextUsagePercent);

        /// <summary>ONE ADDITIONAL STREAM over a worker id, plus the classified completion it reached.</summary>
        /// <param name="Reader">Its own request reader.</param>
        /// <param name="Writer">Its own response writer observation.</param>
        /// <param name="Producer">Its retained producer task.</param>
        /// <param name="WorkerId">The worker id its first message named.</param>
        public sealed record SecondStream(
            ChannelStreamReader Reader,
            DuplicateObservingWriter Writer,
            Task Producer,
            string WorkerId)
        {
            /// <summary>How the stream terminated, or <c>null</c> when it was never awaited.</summary>
            public StreamCompletion? Completion { get; set; }
        }

        /// <summary>HOW A SECOND STREAM ENDED; a losing stream must return normally, never fault.</summary>
        /// <param name="Faulted">Whether the stream ended by throwing rather than returning.</param>
        /// <param name="Fault">The observed failure, when <paramref name="Faulted"/> is <c>true</c>.</param>
        public sealed record StreamCompletion(bool Faulted, Exception? Fault);
    }

    /// <summary>
    /// THE CONFIRMATION-FORWARDING DECORATOR over the REAL <see cref="WorkerCompletionRecorder"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IT FORWARDS BOTH OPERATIONS. <c>Record</c> and <c>ConfirmStoredReceipt</c> are delegated to the
    /// real recorder, so the durable evidence the duplicate branch authorizes against is genuinely
    /// production-written and a matching duplicate really matches. It adds only observation and two
    /// deterministic seams: a ONE-SHOT confirmation fault, and a synchronous mutation that runs
    /// immediately BEFORE the confirmation returns (which is how an ABA during the read is produced).
    /// </para>
    /// <para>
    /// THE COUNTERS COUNT ATTEMPTS, NOT OUTCOMES: a vector that injects a fault still needs to prove
    /// the read was really reached.
    /// </para>
    /// </remarks>
    internal sealed class FaultInjectingConfirmRecorder : IWorkerCompletionRecorder
    {
        private readonly IWorkerCompletionRecorder _inner;
        private int _recordCalls;
        private int _confirmCalls;

        /// <summary>Initialises the decorator over the REAL recorder.</summary>
        /// <param name="inner">The real recorder both operations are delegated to.</param>
        public FaultInjectingConfirmRecorder(IWorkerCompletionRecorder inner) => _inner = inner;

        /// <summary>
        /// A ONE-SHOT fault thrown in place of the real confirmation, or <c>null</c>. It is consumed as
        /// it fires, so a following delivery reaches the real implementation.
        /// </summary>
        public Action? ConfirmFault { get; set; }

        /// <summary>
        /// A synchronous mutation run IMMEDIATELY BEFORE the real confirmation returns — the
        /// deterministic seam that reproduces an ownership change during the read.
        /// </summary>
        public Action? BeforeReturn { get; set; }

        /// <summary>How many times the real <c>Record</c> was invoked through this decorator.</summary>
        public int RecordCalls => Volatile.Read(ref _recordCalls);

        /// <summary>How many times a confirmation was attempted through this decorator.</summary>
        public int ConfirmCalls => Volatile.Read(ref _confirmCalls);

        /// <summary>Zeroes both counters, so a vector can count one delivery's own calls.</summary>
        public void ResetCounts()
        {
            Interlocked.Exchange(ref _recordCalls, 0);
            Interlocked.Exchange(ref _confirmCalls, 0);
        }

        /// <inheritdoc />
        public void Record(string workerId, WorkTask task, TaskResult result)
        {
            Interlocked.Increment(ref _recordCalls);
            _inner.Record(workerId, task, result);
        }

        /// <inheritdoc />
        public bool ConfirmStoredReceipt(string workerId, string taskId, TaskResult result)
        {
            Interlocked.Increment(ref _confirmCalls);

            var fault = ConfirmFault;
            if (fault is not null)
            {
                ConfirmFault = null;
                fault();
            }

            var answer = _inner.ConfirmStoredReceipt(workerId, taskId, result);

            var mutation = BeforeReturn;
            if (mutation is not null)
            {
                BeforeReturn = null;
                mutation();
            }

            return answer;
        }
    }

    /// <summary>
    /// THE REAL COMPLETION-RECEIPT STORES over a private in-memory SQLite database, plus the anchor
    /// connection that keeps it alive for the fixture's lifetime.
    /// </summary>
    private sealed class AckStores : IDisposable
    {
        private readonly SqliteConnection _connection;

        private AckStores(
            IDbContextFactory<CopilotHiveDbContext> factory, SqliteConnection connection)
        {
            _connection = connection;
            AssignmentStore = new WorkerAssignmentContextStore(
                factory, NullLogger<WorkerAssignmentContextStore>.Instance);
            ReceiptStore = new CompletionReceiptStore(
                factory, NullLogger<CompletionReceiptStore>.Instance);
        }

        public WorkerAssignmentContextStore AssignmentStore { get; }

        public CompletionReceiptStore ReceiptStore { get; }

        /// <summary>Creates the stores over a fresh in-memory database.</summary>
        /// <returns>The live store fixture.</returns>
        public static AckStores Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite(connection)
                .Options;

            using (var bootstrap = new CopilotHiveDbContext(options))
                bootstrap.Database.EnsureCreated();

            return new AckStores(new SharedDbContextFactory(connection, options), connection);
        }

        public void Dispose() => _connection.Dispose();
    }

    /// <summary>
    /// THE PUBLICATION OBSERVATION POINT for the acknowledgement: the gRPC writer the real
    /// <c>WorkStream</c> pump forwards every queued <see cref="OrchestratorMessage"/> to.
    /// </summary>
    /// <remarks>
    /// This is where "was an acknowledgement forwarded to this worker" is observable WITHOUT competing
    /// with the production pump for the worker's message channel. It records what it is given and
    /// signals per acknowledgement; it never intercepts, delays or drops anything.
    /// </remarks>
    private sealed class DuplicateObservingWriter : IServerStreamWriter<OrchestratorMessage>
    {
        private readonly List<CompletionReceiptAck> _acknowledgements = [];
        private readonly List<(Func<OrchestratorMessage, bool> Predicate, TaskCompletionSource<OrchestratorMessage> Signal)>
            _messageWaiters = [];

        public WriteOptions? WriteOptions { get; set; }

        /// <summary>Every acknowledgement the transport forwarded, in order.</summary>
        /// <returns>A snapshot of the forwarded acknowledgements.</returns>
        public IReadOnlyList<CompletionReceiptAck> Acknowledgements()
        {
            lock (_acknowledgements)
                return [.. _acknowledgements];
        }

        /// <summary>Clears the acknowledgement ledger, so a vector counts one phase's own publications.</summary>
        public void Clear()
        {
            lock (_acknowledgements)
                _acknowledgements.Clear();
        }

        /// <summary>
        /// A FRESH signal completed by the NEXT forwarded message satisfying <paramref name="predicate"/>,
        /// allocated BEFORE the message is produced so a publication can never be missed.
        /// </summary>
        /// <param name="predicate">Selects the forwarded message the caller waits for.</param>
        /// <returns>A task completing with the matching message.</returns>
        public Task<OrchestratorMessage> WaitForMessage(Func<OrchestratorMessage, bool> predicate)
        {
            var signal = new TaskCompletionSource<OrchestratorMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_acknowledgements)
                _messageWaiters.Add((predicate, signal));
            return signal.Task;
        }

        private Task RecordAsync(OrchestratorMessage message)
        {
            List<TaskCompletionSource<OrchestratorMessage>> matched = [];

            lock (_acknowledgements)
            {
                if (message.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck)
                    _acknowledgements.Add(message.CompletionReceiptAck);

                for (var i = _messageWaiters.Count - 1; i >= 0; i--)
                {
                    if (!_messageWaiters[i].Predicate(message))
                        continue;

                    matched.Add(_messageWaiters[i].Signal);
                    _messageWaiters.RemoveAt(i);
                }
            }

            foreach (var signal in matched)
                signal.TrySetResult(message);

            return Task.CompletedTask;
        }

        Task IAsyncStreamWriter<OrchestratorMessage>.WriteAsync(OrchestratorMessage message) =>
            RecordAsync(message);

        Task IAsyncStreamWriter<OrchestratorMessage>.WriteAsync(
            OrchestratorMessage message, CancellationToken cancellationToken) =>
            RecordAsync(message);
    }

    /// <summary>
    /// Records every logged message and hands out FRESH per-call signals for the production lines the
    /// duplicate vectors synchronize on. A previously emitted line can never satisfy a later wait, and
    /// a fragment can be ARMED to throw so a guarded diagnostic is proven not to escape.
    /// </summary>
    private sealed class SignallingLogger : ILogger<HiveOrchestratorService>
    {
        private readonly List<string> _messages = [];
        private readonly List<(string Fragment, TaskCompletionSource Signal)> _waiters = [];
        private readonly ConcurrentDictionary<string, byte> _armed = new();

        private int _throwCount;

        /// <summary>How many writes actually threw — the proof the fallible diagnostic really ran.</summary>
        public int ThrowCount => Volatile.Read(ref _throwCount);

        /// <summary>Arms a throw for any message containing <paramref name="fragment"/>.</summary>
        /// <param name="fragment">The fragment a throwing write must contain.</param>
        public void ArmThrowOnFragment(string fragment) => _armed[fragment] = 0;

        /// <summary>A snapshot of every message logged so far.</summary>
        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
        }

        /// <summary>A FRESH signal completed by the NEXT message containing <paramref name="fragment"/>.</summary>
        /// <param name="fragment">The fragment the next matching message must contain.</param>
        /// <returns>A task completing when that message is logged.</returns>
        public Task WaitFor(string fragment)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_messages)
                _waiters.Add((fragment, signal));
            return signal.Task;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);

            List<TaskCompletionSource> matched = [];
            var faulted = false;
            lock (_messages)
            {
                _messages.Add(message);

                // ARMED FAULTS ARE COUNTED BEFORE any waiter is released: a test that awaits the
                // diagnostic's own signal must never observe a counter that has not yet been
                // incremented. The THROW itself still happens after the waiters are released, so
                // the production code really emitted the diagnostic and the FAULT is what its
                // guard must survive.
                foreach (var fragment in _armed.Keys)
                {
                    if (!message.Contains(fragment, StringComparison.Ordinal))
                        continue;

                    Interlocked.Increment(ref _throwCount);
                    faulted = true;
                }

                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (!message.Contains(_waiters[i].Fragment, StringComparison.Ordinal))
                        continue;

                    matched.Add(_waiters[i].Signal);
                    _waiters.RemoveAt(i);
                }
            }

            foreach (var signal in matched)
                signal.TrySetResult();

            if (faulted)
                throw new InvalidOperationException("the logger itself threw SENTINEL");
        }
    }

    /// <summary>In-memory stream reader backed by an unbounded channel.</summary>
    private sealed class ChannelStreamReader : IAsyncStreamReader<WorkerMessage>
    {
        private readonly System.Threading.Channels.Channel<WorkerMessage> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<WorkerMessage>();

        public WorkerMessage Current { get; private set; } = new();

        public void Push(WorkerMessage message) => _channel.Writer.TryWrite(message);

        public void Complete() => _channel.Writer.TryComplete();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                if (_channel.Reader.TryRead(out var message))
                {
                    Current = message;
                    return true;
                }
            }

            return false;
        }
    }
}
