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
        [PacketHandler(Opcode.SMSG_SET_PROFICIENCY)]
        void HandleSetProficiency(WorldPacket packet)
        {
            SetProficiency proficiency = new SetProficiency();
            proficiency.ProficiencyClass = packet.ReadUInt8();
            proficiency.ProficiencyMask = packet.ReadUInt32();
            SendPacketToClient(proficiency);
        }
        [PacketHandler(Opcode.SMSG_BUY_SUCCEEDED)]
        void HandleBuySucceeded(WorldPacket packet)
        {
            BuySucceeded buy = new BuySucceeded();
            buy.VendorGUID = packet.ReadGuid().To128();
            buy.Slot = packet.ReadUInt32();
            buy.NewQuantity = packet.ReadInt32();
            buy.QuantityBought = packet.ReadUInt32();
            SendPacketToClient(buy);
        }
        [PacketHandler(Opcode.SMSG_ITEM_PUSH_RESULT)]
        void HandleItemPushResult(WorldPacket packet)
        {
            ItemPushResult item = new ItemPushResult();
            item.PlayerGUID = packet.ReadGuid().To128();
            if (packet.ReadUInt32() == 1)
                item.DisplayText = ItemPushResult.DisplayType.Normal;
            else
                item.DisplayText = ItemPushResult.DisplayType.EncounterLoot;
            item.Created = packet.ReadUInt32() == 1;
            if (packet.ReadUInt32() == 0)
                item.DisplayText = ItemPushResult.DisplayType.Hidden;
            item.SlotInBag = packet.ReadUInt8();
            item.Slot = (byte)packet.ReadUInt32();
            item.Item.ItemID = packet.ReadUInt32();
            item.Item.RandomPropertiesSeed = packet.ReadUInt32();
            item.Item.RandomPropertiesID = packet.ReadUInt32();
            item.Quantity = packet.ReadUInt32();
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                item.QuantityInInventory = packet.ReadUInt32();
            item.ItemGUID = WowGuid128.Empty;
            SendPacketToClient(item);
        }
        [PacketHandler(Opcode.SMSG_READ_ITEM_RESULT_OK)]
        void HandleReadItemResultOk(WorldPacket packet)
        {
            ReadItemResultOK read = new ReadItemResultOK();
            read.ItemGUID = packet.ReadGuid().To128();
            SendPacketToClient(read);
        }
        [PacketHandler(Opcode.SMSG_READ_ITEM_RESULT_FAILED)]
        void HandleReadItemResultFailed(WorldPacket packet)
        {
            ReadItemResultFailed read = new ReadItemResultFailed();
            read.ItemGUID = packet.ReadGuid().To128();
            read.Subcode = 2;
            SendPacketToClient(read);
        }
        [PacketHandler(Opcode.SMSG_BUY_FAILED)]
        void HandleBuyFailed(WorldPacket packet)
        {
            BuyFailed fail = new BuyFailed();
            fail.VendorGUID = packet.ReadGuid().To128();
            fail.Slot = packet.ReadUInt32();
            fail.Reason = (BuyResult)packet.ReadUInt8();
            SendPacketToClient(fail);
        }
    }
}
