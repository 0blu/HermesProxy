﻿using Framework;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;

namespace HermesProxy.World.Client
{
    public partial class WorldClient
    {
        // Handlers for SMSG opcodes coming the legacy world server
        [PacketHandler(Opcode.SMSG_GAME_OBJECT_DESPAWN)]
        void HandleGameObjectDespawn(WorldPacket packet)
        {
            // disabled because mangos reuses same guid on respawn
            // object will remain invisible forever if we've sent despawn
            /*
            GameObjectDespawn despawn = new GameObjectDespawn();
            despawn.ObjectGUID = packet.ReadGuid().To128();
            SendPacketToClient(despawn);
            */
        }

        [PacketHandler(Opcode.SMSG_GAME_OBJECT_RESET_STATE)]
        void HandleGameObjectResetState(WorldPacket packet)
        {
            GameObjectResetState reset = new GameObjectResetState();
            reset.ObjectGUID = packet.ReadGuid().To128();
            SendPacketToClient(reset);
        }

        [PacketHandler(Opcode.SMSG_GAME_OBJECT_CUSTOM_ANIM)]
        void HandleGameObjectCustomAnim(WorldPacket packet)
        {
            GameObjectCustomAnim anim = new GameObjectCustomAnim();
            anim.ObjectGUID = packet.ReadGuid().To128();
            anim.CustomAnim = packet.ReadUInt32();
            SendPacketToClient(anim);
        }
    }
}
