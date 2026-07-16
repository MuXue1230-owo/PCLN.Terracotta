using System.Text.Json;
using Cn.Pcln.Terracotta.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cn.Pcln.Terracotta.Contracts.Tests;

[TestClass]
public sealed class ContractTests
{
    [TestMethod]
    public void RoomCodeNormalizesSupportedInput()
    {
        RoomCode code = new("ab12-cd34-ef56");

        Assert.AreEqual("AB12-CD34-EF56", code.Value);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("ABCD-EFGH")]
    [DataRow("ABCD-EFGH-IJK!")]
    [DataRow("ABCD-EFGH-IJKLM")]
    public void RoomCodeRejectsInvalidInput(string value) =>
        Assert.IsFalse(RoomCode.TryParse(value, out _));

    [TestMethod]
    public void IpcEnvelopeRoundTripsPayloadAndIgnoresUnknownFields()
    {
        IpcEnvelope envelope = IpcEnvelope.Create(
            HelperMessageTypes.Hello,
            new HelperHelloRequest("one-time-token", "pcln", "0.1.0"),
            "request-1");
        string json = JsonSerializer.Serialize(envelope);
        string withUnknownField = json[..^1] + ",\"future\":true}";

        IpcEnvelope restored = JsonSerializer.Deserialize<IpcEnvelope>(withUnknownField)!;
        HelperHelloRequest payload = restored.ReadPayload<HelperHelloRequest>();

        Assert.AreEqual(ProtocolVersion.Current, restored.Protocol);
        Assert.AreEqual("request-1", restored.Id);
        Assert.AreEqual(HelperMessageTypes.Hello, restored.Type);
        Assert.AreEqual("one-time-token", payload.AuthToken);
    }

    [TestMethod]
    public void IdleSnapshotHasNoActiveRoomIdentity()
    {
        TerracottaRoomSnapshot snapshot = TerracottaRoomSnapshot.Idle;

        Assert.AreEqual(TerracottaRoomState.Idle, snapshot.State);
        Assert.AreEqual(TerracottaRoomRole.None, snapshot.Role);
        Assert.IsNull(snapshot.RoomCode);
        Assert.AreEqual(0, snapshot.Members.Count);
    }
}
