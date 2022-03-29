﻿using Framework.Constants;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Server
{
    public partial class WorldSocket
    {
        // Handlers for CMSG opcodes coming from the modern client
        [PacketHandler(Opcode.CMSG_DB_QUERY_BULK)]
        void HandleDbQueryBulk(DBQueryBulk query)
        {
            foreach (uint id in query.Queries)
            {
                DBReply reply = new();
                reply.RecordID = id;
                reply.TableHash = query.TableHash;
                reply.Status = HotfixStatus.Invalid;
                reply.Timestamp = (uint)Time.UnixTime;

                if (query.TableHash == DB2Hash.BroadcastText)
                {
                    BroadcastText bct = GameData.GetBroadcastText(id);
                    if (bct == null)
                    {
                        bct = new BroadcastText();
                        bct.Entry = id;
                        bct.MaleText = "Clear your cache!";
                        bct.FemaleText = "Clear your cache!";
                    }

                    reply.Status = HotfixStatus.Valid;
                    reply.Data.WriteCString(bct.MaleText);
                    reply.Data.WriteCString(bct.FemaleText);
                    reply.Data.WriteUInt32(bct.Entry);
                    reply.Data.WriteUInt32(bct.Language);
                    reply.Data.WriteUInt32(0); // ConditionId
                    reply.Data.WriteUInt16(0); // EmotesId
                    reply.Data.WriteUInt8(0); // Flags
                    reply.Data.WriteUInt32(0); // ChatBubbleDurationMs
                    for (int i = 0; i < 2; ++i)
                        reply.Data.WriteUInt32(0); // SoundEntriesID
                    for (int i = 0; i < 3; ++i)
                        reply.Data.WriteUInt16(bct.Emotes[i]);
                    for (int i = 0; i < 3; ++i)
                        reply.Data.WriteUInt16(bct.EmoteDelays[i]);
                }
                SendPacket(reply);
            }
        }

        [PacketHandler(Opcode.CMSG_HOTFIX_REQUEST)]
        void HandleHotfixRequest(HotfixRequest request)
        {
            HotfixConnect connect = new HotfixConnect();
            foreach (uint id in request.Hotfixes)
            {
                HotfixRecord record;
                if (GameData.Hotfixes.TryGetValue(id, out record))
                {
                    Log.Print(LogType.Debug, $"Hotfix record {record.RecordId} from {record.TableHash}.");
                    connect.Hotfixes.Add(record);
                }
            }
            SendPacket(connect);
        }
    }
}
