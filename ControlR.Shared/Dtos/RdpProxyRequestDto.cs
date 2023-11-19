﻿using ControlR.Shared.Serialization;
using MessagePack;

namespace ControlR.Shared.Dtos;

[MessagePackObject]
public record RdpProxyRequestDto([property: MsgPackKey] Guid SessionId);