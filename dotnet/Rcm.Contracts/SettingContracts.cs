using System.Text.Json.Serialization;

namespace Rcm.Contracts;

public sealed record SettingDto(string Key, string Value, string? Label, long Version);
public sealed record SettingFeatures(bool Read, bool Write);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record UpdateSetting(Guid RequestId, long ExpectedVersion, string NewValue);
