using System.Text.Json.Serialization;

namespace SyncCue.Models;

public sealed class RoomMember
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "member";
    [JsonPropertyName("ready")] public bool Ready { get; set; }
    [JsonPropertyName("connected")] public bool Connected { get; set; }
}

public sealed class RoomRound
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("targetServerMs")] public double TargetServerMs { get; set; }
}

public sealed class RoomState
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("durationMs")] public int DurationMs { get; set; }
    [JsonPropertyName("hostId")] public string HostId { get; set; } = "";
    [JsonPropertyName("round")] public RoomRound? Round { get; set; }
    [JsonPropertyName("members")] public List<RoomMember> Members { get; set; } = new();
}

public sealed class SessionInfo
{
    public string Code { get; set; } = "";
    public string MemberId { get; set; } = "";
    public string Token { get; set; } = "";
}
