﻿using Framework.Constants.World;
using HermesProxy.World.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using World;
using World.Packets;
using static HermesProxy.World.Client.WorldClient;

namespace HermesProxy.World.Client
{
    public partial class WorldClient
    {
        // Handlers for SMSG opcodes coming the legacy world server
        [PacketHandler(Opcode.SMSG_ENUM_CHARACTERS_RESULT)]
        void HandleEnumCharactersResult(WorldPacket packet, List<ServerPacket> responses)
        {
            EnumCharactersResult charEnum = new();
            charEnum.Success = true;
            charEnum.IsDeletedCharacters = false;
            charEnum.IsNewPlayerRestrictionSkipped = false;
            charEnum.IsNewPlayerRestricted = false;
            charEnum.IsNewPlayer = true;
            charEnum.IsAlliedRacesCreationAllowed = false;

            byte count = packet.ReadUInt8();
            for (byte i = 0; i < count; i++)
            {
                EnumCharactersResult.CharacterInfo char1 = new EnumCharactersResult.CharacterInfo();
                packet.ReadGuid().To128();
                char1.Guid = new HermesProxy.WowGuid128(Framework.Constants.World.HighGuidType703.Player, (ulong)i+1);
                char1.Name = packet.ReadCString();
                char1.RaceId = packet.ReadUInt8();
                char1.ClassId = packet.ReadUInt8();
                char1.SexId = packet.ReadUInt8();

                byte skin = packet.ReadUInt8();
                byte face = packet.ReadUInt8();
                byte hairStyle = packet.ReadUInt8();
                byte hairColor = packet.ReadUInt8();
                byte facialHair = packet.ReadUInt8();
                char1.Customizations = CharacterCustomizations.ConvertLegacyCustomizationsToModern((Race)char1.RaceId, (Gender)char1.SexId, skin, face, hairStyle, hairColor, facialHair);

                char1.ExperienceLevel = packet.ReadUInt8();
                if (char1.ExperienceLevel > charEnum.MaxCharacterLevel)
                    charEnum.MaxCharacterLevel = char1.ExperienceLevel;

                char1.ZoneId = packet.ReadUInt32();
                char1.MapId = packet.ReadUInt32();
                char1.PreloadPos = packet.ReadVector3();
                uint guildId = packet.ReadUInt32();
                char1.Flags = (CharacterFlags)packet.ReadUInt32();

                if (LegacyVersion.AddedInVersion(Enums.ClientVersionBuild.V3_0_2_9056))
                    char1.Flags2 = packet.ReadUInt32(); // Customization Flags

                char1.FirstLogin = packet.ReadUInt8() != 0;
                char1.PetCreatureDisplayId = packet.ReadUInt32();
                char1.PetExperienceLevel = packet.ReadUInt32();
                char1.PetCreatureFamilyId = packet.ReadUInt32();

                for (int j = EquipmentSlot.Start; j < EquipmentSlot.End; j++)
                {
                    char1.VisualItems[j].DisplayId = packet.ReadUInt32();
                    char1.VisualItems[j].InvType = packet.ReadUInt8();

                    if (LegacyVersion.AddedInVersion(Enums.ClientVersionBuild.V2_0_1_6180))
                        char1.VisualItems[j].DisplayEnchantId = packet.ReadUInt32();
                }

                int bagCount = LegacyVersion.AddedInVersion(Enums.ClientVersionBuild.V3_3_3_11685) ? 4 : 1;
                for (int j = 0; j < bagCount; j++)
                {
                    char1.VisualItems[EquipmentSlot.Bag1 + j].DisplayId = packet.ReadUInt32();
                    char1.VisualItems[EquipmentSlot.Bag1 + j].InvType = packet.ReadUInt8();

                    if (LegacyVersion.AddedInVersion(Enums.ClientVersionBuild.V2_0_1_6180))
                        char1.VisualItems[EquipmentSlot.Bag1 + j].DisplayEnchantId = packet.ReadUInt32();
                }

                // placeholders
                char1.GuildGuid = new HermesProxy.WowGuid128();
                char1.Flags2 = 402685956;
                char1.Flags3 = 855688192;
                char1.Flags4 = 0;
                char1.ProfessionIds[0] = 0;
                char1.ProfessionIds[1] = 0;
                char1.LastPlayedTime = Time.UnixTime;
                char1.SpecID = 0;
                char1.Unknown703 = 55;
                char1.LastLoginVersion = 11400;
                char1.OverrideSelectScreenFileDataID = 0;
                char1.BoostInProgress = false;
                char1.unkWod61x = 0;
                charEnum.Characters.Add(char1);
            }

            charEnum.RaceUnlockData.Add(new EnumCharactersResult.RaceUnlock(1, true, false, false));
            charEnum.RaceUnlockData.Add(new EnumCharactersResult.RaceUnlock(2, true, false, false));
            charEnum.RaceUnlockData.Add(new EnumCharactersResult.RaceUnlock(3, true, false, false));
            charEnum.RaceUnlockData.Add(new EnumCharactersResult.RaceUnlock(4, true, false, false));
            charEnum.RaceUnlockData.Add(new EnumCharactersResult.RaceUnlock(5, true, false, false));
            charEnum.RaceUnlockData.Add(new EnumCharactersResult.RaceUnlock(6, true, false, false));
            charEnum.RaceUnlockData.Add(new EnumCharactersResult.RaceUnlock(7, true, false, false));
            charEnum.RaceUnlockData.Add(new EnumCharactersResult.RaceUnlock(8, true, false, false));
            responses.Add(charEnum);
        }
    }
}
