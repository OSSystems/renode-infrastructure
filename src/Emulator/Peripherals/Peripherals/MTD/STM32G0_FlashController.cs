//
// Copyright (c) 2026 Freedom Veiculos Eletricos
// SPDX-License-Identifier: UNLICENSED
//
// STM32G0 Flash Controller for Renode
//
// The STM32G0 has a reserved word at offset 0x04, shifting all
// registers by 4 bytes compared to STM32F4. This peripheral
// provides correct register offsets and functional page erase
// for STM32G0B1xx simulation.
//
using System;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Memory;

namespace Antmicro.Renode.Peripherals.MTD
{
    [AllowedTranslations(
        AllowedTranslation.ByteToDoubleWord |
        AllowedTranslation.WordToDoubleWord)]
    public class STM32G0_FlashController
        : STM32_FlashController, IKnownSize
    {
        public STM32G0_FlashController(
            IMachine machine,
            MappedMemory flash)
            : base(machine)
        {
            this.flash = flash;

            controlLock = new LockRegister(
                this, nameof(controlLock), ControlLockKey);
            optionControlLock = new LockRegister(
                this, nameof(optionControlLock),
                OptionLockKey);

            DefineRegisters();
            Reset();

            // Real flash powers up in erased state (0xFF).
            // Fill entire MappedMemory so unloaded regions
            // behave like erased flash. This runs only once
            // at construction; flash is non-volatile and must
            // persist across resets.
            var erased = new byte[flash.Size];
            for(int i = 0; i < erased.Length; i++)
            {
                erased[i] = 0xFF;
            }
            flash.WriteBytes(0, erased);
        }

        public override void Reset()
        {
            base.Reset();
            controlLock.Reset();
            optionControlLock.Reset();
        }

        public override void WriteDoubleWord(
            long offset, uint value)
        {
            if((Registers)offset == Registers.Control
                && controlLock.IsLocked)
            {
                this.Log(LogLevel.Warning,
                    "Write 0x{0:X8} to locked Control register"
                    + " ignored", value);
                return;
            }

            base.WriteDoubleWord(offset, value);
        }

        public long Size => 0x400;

        private void DefineRegisters()
        {
            // --- FLASH_ACR (0x00) ---
            Registers.AccessControl.Define(this)
                .WithValueField(0, 3, name: "LATENCY")
                .WithReservedBits(3, 5)
                .WithTaggedFlag("PRFTEN", 8)
                .WithTaggedFlag("ICEN", 9)
                .WithReservedBits(10, 1)
                .WithTaggedFlag("ICRST", 11)
                .WithReservedBits(12, 4)
                .WithTaggedFlag("DBG_SWEN", 16)
                .WithReservedBits(17, 1)
                .WithTaggedFlag("EMPTY", 18)
                .WithReservedBits(19, 13);

            // --- FLASH_KEYR (0x08) ---
            Registers.Key.Define(this)
                .WithValueField(0, 32, FieldMode.Write,
                    name: "FLASH_KEYR",
                    writeCallback: (_, value) =>
                        controlLock.ConsumeValue(
                            (uint)value));

            // --- FLASH_OPTKEYR (0x0C) ---
            Registers.OptionKey.Define(this)
                .WithValueField(0, 32, FieldMode.Write,
                    name: "FLASH_OPTKEYR",
                    writeCallback: (_, value) =>
                        optionControlLock.ConsumeValue(
                            (uint)value));

            // --- FLASH_SR (0x10) ---
            Registers.Status.Define(this)
                .WithFlag(0, FieldMode.Read | FieldMode.WriteOneToClear,
                    name: "EOP",
                    valueProviderCallback: _ => eop,
                    writeCallback: (_, value) =>
                    {
                        if(value) eop = false;
                    })
                .WithTaggedFlag("OPERR", 1)
                .WithReservedBits(2, 1)
                .WithTaggedFlag("PROGERR", 3)
                .WithTaggedFlag("WRPERR", 4)
                .WithTaggedFlag("PGAERR", 5)
                .WithTaggedFlag("SIZERR", 6)
                .WithTaggedFlag("PGSERR", 7)
                .WithTaggedFlag("MISERR", 8)
                .WithTaggedFlag("FASTERR", 9)
                .WithReservedBits(10, 4)
                .WithTaggedFlag("RDERR", 14)
                .WithTaggedFlag("OPTVERR", 15)
                .WithFlag(16, FieldMode.Read, name: "BSY1",
                    valueProviderCallback: _ => false)
                .WithFlag(17, FieldMode.Read, name: "BSY2",
                    valueProviderCallback: _ => false)
                .WithFlag(18, FieldMode.Read, name: "CFGBSY",
                    valueProviderCallback: _ => false)
                .WithReservedBits(19, 13);

            // --- FLASH_CR (0x14) ---
            Registers.Control.Define(this)
                .WithFlag(0, name: "PG",
                    valueProviderCallback: _ => pgEnabled,
                    writeCallback: (_, value) =>
                        pgEnabled = value)
                .WithFlag(1, name: "PER",
                    valueProviderCallback: _ => perEnabled,
                    writeCallback: (_, value) =>
                        perEnabled = value)
                .WithFlag(2, name: "MER1",
                    valueProviderCallback: _ => mer1Enabled,
                    writeCallback: (_, value) =>
                        mer1Enabled = value)
                .WithValueField(3, 10, name: "PNB",
                    valueProviderCallback: _ => pageNumber,
                    writeCallback: (_, value) =>
                        pageNumber = (uint)value)
                .WithFlag(13, name: "BKER",
                    valueProviderCallback: _ => bankSelect,
                    writeCallback: (_, value) =>
                        bankSelect = value)
                .WithReservedBits(14, 1)
                .WithFlag(15, name: "MER2",
                    valueProviderCallback: _ => mer2Enabled,
                    writeCallback: (_, value) =>
                        mer2Enabled = value)
                .WithFlag(16, FieldMode.Read | FieldMode.Write,
                    name: "STRT",
                    valueProviderCallback: _ => false,
                    writeCallback: (_, value) =>
                    {
                        if(value) ExecuteErase();
                    })
                .WithTaggedFlag("OPTSTRT", 17)
                .WithTaggedFlag("FSTPG", 18)
                .WithReservedBits(19, 5)
                .WithFlag(24, name: "EOPIE",
                    valueProviderCallback: _ => eopieEnabled,
                    writeCallback: (_, value) =>
                        eopieEnabled = value)
                .WithTaggedFlag("ERRIE", 25)
                .WithTaggedFlag("RDERRIE", 26)
                .WithTaggedFlag("OBL_LAUNCH", 27)
                .WithTaggedFlag("SEC_PROT", 28)
                .WithTaggedFlag("SEC_PROT2", 29)
                .WithFlag(30, FieldMode.Read | FieldMode.Set,
                    name: "OPTLOCK",
                    valueProviderCallback: _ =>
                        optionControlLock.IsLocked,
                    changeCallback: (_, value) =>
                    {
                        if(value) optionControlLock.Lock();
                    })
                .WithFlag(31, FieldMode.Read | FieldMode.Set,
                    name: "LOCK",
                    valueProviderCallback: _ =>
                        controlLock.IsLocked,
                    changeCallback: (_, value) =>
                    {
                        if(value) controlLock.Lock();
                    });

            // --- FLASH_ECCR (0x18) ---
            Registers.ECCRegister.Define(this)
                .WithTag("ADDR_ECC", 0, 14)
                .WithReservedBits(14, 6)
                .WithTaggedFlag("SYSF_ECC", 20)
                .WithReservedBits(21, 3)
                .WithTaggedFlag("ECCIE", 24)
                .WithReservedBits(25, 5)
                .WithTaggedFlag("ECCC", 30)
                .WithTaggedFlag("ECCD", 31);

            // --- FLASH_OPTR (0x20) ---
            // Default: nSWAP_BANK=1, DUAL_BANK=1,
            // RDP=0xAA (level 0), typical production bits.
            Registers.OptionRegister.Define(this, 0x3FFFE8AA)
                .WithValueField(0, 8, name: "RDP")
                .WithTaggedFlag("BOR_EN", 8)
                .WithTag("BORR_LEV", 9, 2)
                .WithTag("BORF_LEV", 11, 2)
                .WithTaggedFlag("nRST_STOP", 13)
                .WithTaggedFlag("nRST_STDBY", 14)
                .WithTaggedFlag("nRST_SHDW", 15)
                .WithTaggedFlag("IWDG_SW", 16)
                .WithTaggedFlag("IWDG_STOP", 17)
                .WithTaggedFlag("IWDG_STDBY", 18)
                .WithTaggedFlag("WWDG_SW", 19)
                .WithFlag(20, name: "nSWAP_BANK")
                .WithFlag(21, name: "DUAL_BANK")
                .WithTaggedFlag("RAM_PARITY_CHECK", 22)
                .WithReservedBits(23, 1)
                .WithTaggedFlag("nBOOT_SEL", 24)
                .WithTaggedFlag("nBOOT1", 25)
                .WithTaggedFlag("nBOOT0", 26)
                .WithTag("NRST_MODE", 27, 2)
                .WithTaggedFlag("IRHEN", 29)
                .WithReservedBits(30, 2);
        }

        private void ExecuteErase()
        {
            if(perEnabled)
            {
                // Single page erase.
                // STM32G0 bank 2 physical pages start at
                // PNB 256 (Bank2StartPage). The Zephyr
                // driver writes PNB = (page % 128) + 256
                // with BKER=1 for bank 2. Convert the
                // physical PNB to a linear page offset.
                uint page = pageNumber;
                if(bankSelect)
                {
                    uint pageInBank = (page >= Bank2StartPage)
                        ? page - Bank2StartPage
                        : page;
                    page = PagesPerBank + pageInBank;
                }
                uint offset = page * PageSize;

                if(offset + PageSize > flash.Size)
                {
                    this.Log(LogLevel.Error,
                        "Page erase out of range: page={0}"
                        + " offset=0x{1:X}", page, offset);
                    perEnabled = false;
                    return;
                }

                this.Log(LogLevel.Debug,
                    "Erasing page {0} (offset 0x{1:X},"
                    + " size 0x{2:X})",
                    page, offset, PageSize);

                var eraseData = Enumerable.Repeat(
                    (byte)0xFF, (int)PageSize).ToArray();
                flash.WriteBytes(offset, eraseData);

                if(eopieEnabled) eop = true;
                perEnabled = false;
            }
            else if(mer1Enabled)
            {
                // Mass erase bank 1
                uint bank1Size = PagesPerBank * PageSize;
                if(bank1Size > flash.Size)
                {
                    bank1Size = (uint)flash.Size;
                }

                this.Log(LogLevel.Info,
                    "Mass erase bank 1 (0x{0:X} bytes)",
                    bank1Size);

                var eraseData = Enumerable.Repeat(
                    (byte)0xFF, (int)bank1Size).ToArray();
                flash.WriteBytes(0, eraseData);

                if(eopieEnabled) eop = true;
                mer1Enabled = false;
            }
            else if(mer2Enabled)
            {
                // Mass erase bank 2
                uint bank1Size = PagesPerBank * PageSize;
                if(bank1Size >= flash.Size)
                {
                    this.Log(LogLevel.Warning,
                        "Mass erase bank 2 requested but"
                        + " flash has no bank 2");
                    return;
                }

                uint bank2Size = (uint)flash.Size - bank1Size;
                this.Log(LogLevel.Info,
                    "Mass erase bank 2 (offset 0x{0:X},"
                    + " 0x{1:X} bytes)",
                    bank1Size, bank2Size);

                var eraseData = Enumerable.Repeat(
                    (byte)0xFF, (int)bank2Size).ToArray();
                flash.WriteBytes(bank1Size, eraseData);

                if(eopieEnabled) eop = true;
                mer2Enabled = false;
            }
            else
            {
                this.Log(LogLevel.Warning,
                    "STRT set but no erase mode selected"
                    + " (PER/MER1/MER2 all clear)");
            }
        }

        private readonly MappedMemory flash;

        private readonly LockRegister controlLock;
        private readonly LockRegister optionControlLock;

        // CR field backing stores
        private bool pgEnabled;
        private bool perEnabled;
        private bool mer1Enabled;
        private bool mer2Enabled;
        private bool bankSelect;
        private uint pageNumber;

        // SR.EOP flag (only set when CR.EOPIE is enabled)
        private bool eop;
        private bool eopieEnabled;

        private const uint PageSize = 2048;
        private const uint PagesPerBank = 128;
        private const uint Bank2StartPage = 256;

        private static readonly uint[] ControlLockKey =
            { 0x45670123, 0xCDEF89AB };
        private static readonly uint[] OptionLockKey =
            { 0x08192A3B, 0x4C5D6E7F };

        private enum Registers
        {
            AccessControl  = 0x00, // FLASH_ACR
            // 0x04 reserved
            Key            = 0x08, // FLASH_KEYR
            OptionKey      = 0x0C, // FLASH_OPTKEYR
            Status         = 0x10, // FLASH_SR
            Control        = 0x14, // FLASH_CR
            ECCRegister    = 0x18, // FLASH_ECCR
            // 0x1C ECC2R (not needed)
            OptionRegister = 0x20, // FLASH_OPTR
        }
    }
}
