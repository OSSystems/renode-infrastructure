//
// Copyright (c) 2026 Freedom Veiculos Eletricos
// SPDX-License-Identifier: UNLICENSED
//
// STM32G0 Power Controller for Renode
//
// Register map per STM32G0B1xx Reference Manual (RM0444).
// Models CR1-CR4, SR1, SR2, SCR and pull-up/pull-down
// registers at the correct offsets for the G0 family.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    [AllowedTranslations(
        AllowedTranslation.ByteToDoubleWord |
        AllowedTranslation.WordToDoubleWord)]
    public class STM32G0_PWR
        : BasicDoubleWordPeripheral, IKnownSize
    {
        public STM32G0_PWR(IMachine machine)
            : base(machine)
        {
            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            base.Reset();
        }

        public long Size => 0x400;

        private void DefineRegisters()
        {
            // --- PWR_CR1 (0x00) reset: 0x00000208 ---
            // VOS reset = 01 (range 1) at bits [10:9]
            Registers.CR1.Define(this, 0x00000208)
                .WithValueField(0, 3, name: "LPMS")
                .WithFlag(3, name: "FPD_STOP")
                .WithFlag(4, name: "FPD_LPRUN")
                .WithFlag(5, name: "FPD_LPSLP")
                .WithReservedBits(6, 2)
                .WithFlag(8, name: "DBP")
                .WithValueField(9, 2, name: "VOS")
                .WithReservedBits(11, 3)
                .WithFlag(14, name: "LPR")
                .WithReservedBits(15, 17);

            // --- PWR_CR2 (0x04) reset: 0x00000000 ---
            Registers.CR2.Define(this)
                .WithFlag(0, name: "PVDE")
                .WithValueField(1, 3, name: "PVDFT")
                .WithValueField(4, 3, name: "PVDRT")
                .WithReservedBits(7, 1)
                .WithFlag(8, name: "PVMEN_USB")
                .WithFlag(9, name: "IOSV")
                .WithFlag(10, name: "USV")
                .WithReservedBits(11, 21);

            // --- PWR_CR3 (0x08) reset: 0x00008000 ---
            // EIWUL reset = 1
            Registers.CR3.Define(this, 0x00008000)
                .WithFlag(0, name: "EWUP1")
                .WithFlag(1, name: "EWUP2")
                .WithFlag(2, name: "EWUP3")
                .WithFlag(3, name: "EWUP4")
                .WithFlag(4, name: "EWUP5")
                .WithFlag(5, name: "EWUP6")
                .WithReservedBits(6, 2)
                .WithFlag(8, name: "RRS")
                .WithFlag(9, name: "ENB_ULP")
                .WithFlag(10, name: "APC")
                .WithReservedBits(11, 4)
                .WithFlag(15, name: "EIWUL")
                .WithReservedBits(16, 16);

            // --- PWR_CR4 (0x0C) reset: 0x00000000 ---
            Registers.CR4.Define(this)
                .WithFlag(0, name: "WP1")
                .WithFlag(1, name: "WP2")
                .WithFlag(2, name: "WP3")
                .WithFlag(3, name: "WP4")
                .WithFlag(4, name: "WP5")
                .WithFlag(5, name: "WP6")
                .WithReservedBits(6, 2)
                .WithFlag(8, name: "VBE")
                .WithFlag(9, name: "VBRS")
                .WithReservedBits(10, 22);

            // --- PWR_SR1 (0x10) reset: 0x00000000 ---
            Registers.SR1.Define(this)
                .WithFlag(0, out wuf1, FieldMode.Read,
                    name: "WUF1")
                .WithFlag(1, out wuf2, FieldMode.Read,
                    name: "WUF2")
                .WithFlag(2, out wuf3, FieldMode.Read,
                    name: "WUF3")
                .WithFlag(3, out wuf4, FieldMode.Read,
                    name: "WUF4")
                .WithFlag(4, out wuf5, FieldMode.Read,
                    name: "WUF5")
                .WithFlag(5, out wuf6, FieldMode.Read,
                    name: "WUF6")
                .WithReservedBits(6, 2)
                .WithFlag(8, out sbf, FieldMode.Read,
                    name: "SBF")
                .WithReservedBits(9, 6)
                .WithFlag(15, FieldMode.Read,
                    valueProviderCallback: _ => false,
                    name: "WUFI")
                .WithReservedBits(16, 16);

            // --- PWR_SR2 (0x14) reset: 0x00000000 ---
            Registers.SR2.Define(this)
                .WithReservedBits(0, 7)
                .WithFlag(7, FieldMode.Read,
                    valueProviderCallback: _ => true,
                    name: "FLASH_RDY")
                .WithFlag(8, FieldMode.Read,
                    valueProviderCallback: _ => false,
                    name: "REGLPS")
                .WithFlag(9, FieldMode.Read,
                    valueProviderCallback: _ => false,
                    name: "REGLPF")
                .WithFlag(10, FieldMode.Read,
                    valueProviderCallback: _ => false,
                    name: "VOSF")
                .WithFlag(11, FieldMode.Read,
                    valueProviderCallback: _ => false,
                    name: "PVDO")
                .WithReservedBits(12, 1)
                .WithFlag(13, FieldMode.Read,
                    valueProviderCallback: _ => false,
                    name: "PVMO_USB")
                .WithReservedBits(14, 18);

            // --- PWR_SCR (0x18) reset: 0x00000000 ---
            Registers.SCR.Define(this)
                .WithFlag(0, FieldMode.Write,
                    writeCallback: (_, v) =>
                    { if(v) wuf1.Value = false; },
                    name: "CWUF1")
                .WithFlag(1, FieldMode.Write,
                    writeCallback: (_, v) =>
                    { if(v) wuf2.Value = false; },
                    name: "CWUF2")
                .WithFlag(2, FieldMode.Write,
                    writeCallback: (_, v) =>
                    { if(v) wuf3.Value = false; },
                    name: "CWUF3")
                .WithFlag(3, FieldMode.Write,
                    writeCallback: (_, v) =>
                    { if(v) wuf4.Value = false; },
                    name: "CWUF4")
                .WithFlag(4, FieldMode.Write,
                    writeCallback: (_, v) =>
                    { if(v) wuf5.Value = false; },
                    name: "CWUF5")
                .WithFlag(5, FieldMode.Write,
                    writeCallback: (_, v) =>
                    { if(v) wuf6.Value = false; },
                    name: "CWUF6")
                .WithReservedBits(6, 2)
                .WithFlag(8, FieldMode.Write,
                    writeCallback: (_, v) =>
                    { if(v) sbf.Value = false; },
                    name: "CSBF")
                .WithReservedBits(9, 23);

            // --- Pull-up/pull-down registers ---
            // Tagged as simple R/W; no behavioral model
            // needed for simulation.
            Registers.PUCRA.Define(this)
                .WithValueField(0, 16, name: "PU");
            Registers.PDCRA.Define(this)
                .WithValueField(0, 16, name: "PD");
            Registers.PUCRB.Define(this)
                .WithValueField(0, 16, name: "PU");
            Registers.PDCRB.Define(this)
                .WithValueField(0, 16, name: "PD");
            Registers.PUCRC.Define(this)
                .WithValueField(0, 16, name: "PU");
            Registers.PDCRC.Define(this)
                .WithValueField(0, 16, name: "PD");
            Registers.PUCRD.Define(this)
                .WithValueField(0, 16, name: "PU");
            Registers.PDCRD.Define(this)
                .WithValueField(0, 16, name: "PD");
            Registers.PUCRE.Define(this)
                .WithValueField(0, 16, name: "PU");
            Registers.PDCRE.Define(this)
                .WithValueField(0, 16, name: "PD");
            Registers.PUCRF.Define(this)
                .WithValueField(0, 16, name: "PU");
            Registers.PDCRF.Define(this)
                .WithValueField(0, 16, name: "PD");
        }

        private IFlagRegisterField wuf1;
        private IFlagRegisterField wuf2;
        private IFlagRegisterField wuf3;
        private IFlagRegisterField wuf4;
        private IFlagRegisterField wuf5;
        private IFlagRegisterField wuf6;
        private IFlagRegisterField sbf;

        private enum Registers
        {
            CR1   = 0x00,
            CR2   = 0x04,
            CR3   = 0x08,
            CR4   = 0x0C,
            SR1   = 0x10,
            SR2   = 0x14,
            SCR   = 0x18,
            // 0x1C reserved
            PUCRA = 0x20,
            PDCRA = 0x24,
            PUCRB = 0x28,
            PDCRB = 0x2C,
            PUCRC = 0x30,
            PDCRC = 0x34,
            PUCRD = 0x38,
            PDCRD = 0x3C,
            PUCRE = 0x40,
            PDCRE = 0x44,
            PUCRF = 0x48,
            PDCRF = 0x4C,
        }
    }
}
