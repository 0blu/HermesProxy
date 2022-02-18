﻿using Framework.Constants;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;

namespace HermesProxy.World.Server
{
    public partial class WorldSocket
    {
        // Handlers for CMSG opcodes coming from the modern client
        [PacketHandler(Opcode.CMSG_CHAT_JOIN_CHANNEL)]
        void HandleChatJoinChannel(JoinChannel join)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_CHAT_JOIN_CHANNEL);
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                packet.WriteInt32(join.ChatChannelId);
                packet.WriteUInt8(0); // Has Voice
                packet.WriteUInt8(0); // Joined by zone update
            }
            packet.WriteCString(join.ChannelName);
            packet.WriteCString(join.Password);
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_AFK)]
        void HandleChatMessageAFK(ChatMessageAFK afk)
        {
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                Global.CurrentSessionData.WorldClient.SendMessageChatWotLK(ChatMessageTypeWotLK.Afk, 0, afk.Text, "", "");
            else
                Global.CurrentSessionData.WorldClient.SendMessageChatVanilla(ChatMessageTypeVanilla.Afk, 0, afk.Text, "", "");
        }

        [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_DND)]
        void HandleChatMessageDND(ChatMessageDND dnd)
        {
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                Global.CurrentSessionData.WorldClient.SendMessageChatWotLK(ChatMessageTypeWotLK.Dnd, 0, dnd.Text, "", "");
            else
                Global.CurrentSessionData.WorldClient.SendMessageChatVanilla(ChatMessageTypeVanilla.Dnd, 0, dnd.Text, "", "");
        }

        [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_WHISPER)]
        void HandleChatMessageWhisper(ChatMessageWhisper whisper)
        {
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                Global.CurrentSessionData.WorldClient.SendMessageChatWotLK(ChatMessageTypeWotLK.Whisper, whisper.Language, whisper.Text, "", whisper.Target);
            else
                Global.CurrentSessionData.WorldClient.SendMessageChatVanilla(ChatMessageTypeVanilla.Whisper, whisper.Language, whisper.Text, "", whisper.Target);
        }

        [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_GUILD)]
        [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_OFFICER)]
        [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_PARTY)]
        [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_RAID)]
        [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_RAID_WARNING)]
        [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_SAY)]
        [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_YELL)]
        [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_INSTANCE_CHAT)]
        void HandleChatMessage(ChatMessage packet)
        {
            ChatMessageTypeModern type;

            switch (packet.GetUniversalOpcode())
            {
                case Opcode.CMSG_CHAT_MESSAGE_SAY:
                    type = ChatMessageTypeModern.Say;
                    break;
                case Opcode.CMSG_CHAT_MESSAGE_YELL:
                    type = ChatMessageTypeModern.Yell;
                    break;
                case Opcode.CMSG_CHAT_MESSAGE_GUILD:
                    type = ChatMessageTypeModern.Guild;
                    break;
                case Opcode.CMSG_CHAT_MESSAGE_OFFICER:
                    type = ChatMessageTypeModern.Officer;
                    break;
                case Opcode.CMSG_CHAT_MESSAGE_PARTY:
                    type = ChatMessageTypeModern.Party;
                    break;
                case Opcode.CMSG_CHAT_MESSAGE_RAID:
                    type = ChatMessageTypeModern.Raid;
                    break;
                case Opcode.CMSG_CHAT_MESSAGE_RAID_WARNING:
                    type = ChatMessageTypeModern.RaidWarning;
                    break;
                case Opcode.CMSG_CHAT_MESSAGE_INSTANCE_CHAT:
                    type = ChatMessageTypeModern.Party;
                    break;
                default:
                    Log.Print(LogType.Error, $"HandleMessagechatOpcode : Unknown chat opcode ({packet.GetOpcode()})");
                    return;
            }

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                ChatMessageTypeWotLK chatMsg = (ChatMessageTypeWotLK)Enum.Parse(typeof(ChatMessageTypeWotLK), type.ToString());
                Global.CurrentSessionData.WorldClient.SendMessageChatWotLK(chatMsg, packet.Language, packet.Text, "", "");
            }
            else
            {
                ChatMessageTypeVanilla chatMsg = (ChatMessageTypeVanilla)Enum.Parse(typeof(ChatMessageTypeVanilla), type.ToString());
                Global.CurrentSessionData.WorldClient.SendMessageChatVanilla(chatMsg, packet.Language, packet.Text, "", "");
            }
        }
    }
}
