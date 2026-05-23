//
// Copyright (c) 2026 Freedom Veiculos Eletricos
// SPDX-License-Identifier: UNLICENSED
//
// STM32G0 EXTI (External Interrupt) Controller for Renode
//
// The STM32G0 EXTI register layout differs significantly from
// the STM32F4. Key differences:
//   - RTSR/FTSR at 0x00/0x04 (F4: 0x08/0x0C)
//   - Separate rising/falling pending registers (RPR/FPR)
//   - IMR moved to 0x80 (F4: 0x00)
//   - EXTICR mux registers at 0x60-0x6C
//   - Bank 2 registers for lines 32+
//
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.IRQControllers
{
    [AllowedTranslations(
        AllowedTranslation.ByteToDoubleWord |
        AllowedTranslation.WordToDoubleWord)]
    public class STM32G0_EXTI
        : BasicDoubleWordPeripheral, IKnownSize,
          IIRQController, INumberedGPIOOutput
    {
        public STM32G0_EXTI(
            IMachine machine,
            int numberOfOutputLines = 36,
            int firstDirectLine = 19)
            : base(machine)
        {
            var innerConnections =
                new Dictionary<int, IGPIO>();
            for(var i = 0; i < numberOfOutputLines; ++i)
            {
                innerConnections[i] = new GPIO();
            }
            Connections =
                new ReadOnlyDictionary<int, IGPIO>(
                    innerConnections);

            core = new STM32_EXTICore(
                this,
                BitHelper.CalculateQuadWordMask(
                    firstDirectLine, 0),
                treatOutOfRangeLinesAsDirect: true,
                separateConfigs: true,
                allowMaskingDirectLines: false);

            numberOfLinesMask =
                BitHelper.CalculateQuadWordMask(
                    (int)NumberOfLines, 0);

            DefineRegisters();
            Reset();
        }

        public void OnGPIO(int number, bool value)
        {
            if(number >= NumberOfLines)
            {
                this.Log(LogLevel.Error,
                    "GPIO number {0} is out of range"
                    + " [0; {1})",
                    number, NumberOfLines);
                return;
            }
            var lineNumber = (byte)number;

            if(core.CanSetInterruptValue(
                lineNumber, value,
                out var isLineConfigurable))
            {
                value = isLineConfigurable ? true : value;
                core.UpdatePendingValue(lineNumber, value);
                Connections[number].Set(value);
            }
        }

        public override void Reset()
        {
            base.Reset();
            softwareInterrupt1 = 0;
            softwareInterrupt2 = 0;
            foreach(var gpio in Connections)
            {
                gpio.Value.Unset();
            }
        }

        public long Size => 0x400;

        public IReadOnlyDictionary<int, IGPIO> Connections
        { get; }

        public long NumberOfLines => Connections.Count;

        private void DefineRegisters()
        {
            // --- Bank 1 (lines 0-31) ---

            // RTSR1 (0x00)
            Registers.RisingTriggerSelection1
                .Define(this)
                .WithValueField(0, 32,
                    out core.RisingEdgeMask,
                    name: "RTSR1");

            // FTSR1 (0x04)
            Registers.FallingTriggerSelection1
                .Define(this)
                .WithValueField(0, 32,
                    out core.FallingEdgeMask,
                    name: "FTSR1");

            // SWIER1 (0x08)
            Registers.SoftwareInterruptEvent1
                .Define(this)
                .WithValueField(0, 32, name: "SWIER1",
                    valueProviderCallback: _ =>
                        softwareInterrupt1,
                    writeCallback: (_, value) =>
                    {
                        softwareInterrupt1 |= value;
                        value &= numberOfLinesMask;
                        BitHelper.ForeachActiveBit(
                            value, x =>
                            {
                                core.UpdatePendingValue(
                                    (byte)x, true);
                                Connections[x].Set();
                            });
                    });

            // RPR1 (0x0C) - Rising pending
            Registers.RisingPending1.Define(this)
                .WithValueField(0, 32,
                    out core.PendingRaisingInterrupts,
                    FieldMode.Read |
                    FieldMode.WriteOneToClear,
                    name: "RPR1",
                    writeCallback: (_, value) =>
                    {
                        softwareInterrupt1 &= ~value;
                        value &= numberOfLinesMask;
                        BitHelper.ForeachActiveBit(
                            value, x =>
                            {
                                if(!BitHelper.IsBitSet(
                                    core
                                    .PendingFallingInterrupts
                                    .Value, (byte)x))
                                {
                                    Connections[x].Unset();
                                }
                            });
                    });

            // FPR1 (0x10) - Falling pending
            Registers.FallingPending1.Define(this)
                .WithValueField(0, 32,
                    out core.PendingFallingInterrupts,
                    FieldMode.Read |
                    FieldMode.WriteOneToClear,
                    name: "FPR1",
                    writeCallback: (_, value) =>
                    {
                        value &= numberOfLinesMask;
                        BitHelper.ForeachActiveBit(
                            value, x =>
                            {
                                if(!BitHelper.IsBitSet(
                                    core
                                    .PendingRaisingInterrupts
                                    .Value, (byte)x))
                                {
                                    Connections[x].Unset();
                                }
                            });
                    });

            // EXTICR1-4 (0x60-0x6C)
            // GPIO mux selection: which port drives
            // each EXTI line. Tagged for now; the GPIO
            // port connections in the .repl handle
            // routing directly.
            Registers.ExternalInterruptConfig1
                .Define(this)
                .WithValueField(0, 32, name: "EXTICR1");
            Registers.ExternalInterruptConfig2
                .Define(this)
                .WithValueField(0, 32, name: "EXTICR2");
            Registers.ExternalInterruptConfig3
                .Define(this)
                .WithValueField(0, 32, name: "EXTICR3");
            Registers.ExternalInterruptConfig4
                .Define(this)
                .WithValueField(0, 32, name: "EXTICR4");

            // IMR1 (0x80) - reset: 0xFFF80000
            // Lines 19-31 are direct (unmasked at reset)
            Registers.InterruptMask1.Define(this, 0xFFF80000)
                .WithValueField(0, 32,
                    out core.InterruptMask,
                    name: "IMR1");

            // EMR1 (0x84)
            Registers.EventMask1.Define(this)
                .WithValueField(0, 32, name: "EMR1");

            // --- Bank 2 (lines 32-35) ---

            // RTSR2 (0x20)
            Registers.RisingTriggerSelection2
                .Define(this)
                .WithValueField(0, 32, name: "RTSR2");

            // FTSR2 (0x24)
            Registers.FallingTriggerSelection2
                .Define(this)
                .WithValueField(0, 32, name: "FTSR2");

            // SWIER2 (0x28)
            Registers.SoftwareInterruptEvent2
                .Define(this)
                .WithValueField(0, 32, name: "SWIER2",
                    valueProviderCallback: _ =>
                        softwareInterrupt2,
                    writeCallback: (_, value) =>
                    {
                        softwareInterrupt2 |= value;
                        BitHelper.ForeachActiveBit(
                            value, x =>
                            {
                                pendingRisingInterrupts2.Value |= 1UL << x;
                                Connections[x + 32].Set();
                            });
                    });

            // RPR2 (0x2C) - Rising pending
            Registers.RisingPending2.Define(this)
                .WithValueField(0, 32,
                    out pendingRisingInterrupts2,
                    FieldMode.Read |
                    FieldMode.WriteOneToClear,
                    name: "RPR2",
                    writeCallback: (_, value) =>
                    {
                        softwareInterrupt2 &= ~value;
                        BitHelper.ForeachActiveBit(
                            value, x =>
                            {
                                if(!BitHelper.IsBitSet(
                                    pendingFallingInterrupts2
                                    .Value, (byte)x))
                                {
                                    Connections[x + 32].Unset();
                                }
                            });
                    });

            // FPR2 (0x30) - Falling pending
            Registers.FallingPending2.Define(this)
                .WithValueField(0, 32,
                    out pendingFallingInterrupts2,
                    FieldMode.Read |
                    FieldMode.WriteOneToClear,
                    name: "FPR2",
                    writeCallback: (_, value) =>
                    {
                        BitHelper.ForeachActiveBit(
                            value, x =>
                            {
                                if(!BitHelper.IsBitSet(
                                    pendingRisingInterrupts2
                                    .Value, (byte)x))
                                {
                                    Connections[x + 32].Unset();
                                }
                            });
                    });

            // IMR2 (0x90)
            Registers.InterruptMask2.Define(this)
                .WithValueField(0, 32, name: "IMR2");

            // EMR2 (0x94)
            Registers.EventMask2.Define(this)
                .WithValueField(0, 32, name: "EMR2");
        }

        private ulong softwareInterrupt1;
        private ulong softwareInterrupt2;
        private IValueRegisterField pendingRisingInterrupts2;
        private IValueRegisterField pendingFallingInterrupts2;

        private readonly ulong numberOfLinesMask;
        private readonly STM32_EXTICore core;

        private enum Registers
        {
            RisingTriggerSelection1    = 0x00,
            FallingTriggerSelection1   = 0x04,
            SoftwareInterruptEvent1    = 0x08,
            RisingPending1             = 0x0C,
            FallingPending1            = 0x10,
            // 0x14-0x1C reserved
            RisingTriggerSelection2    = 0x20,
            FallingTriggerSelection2   = 0x24,
            SoftwareInterruptEvent2    = 0x28,
            RisingPending2             = 0x2C,
            FallingPending2            = 0x30,
            // 0x34-0x5C reserved
            ExternalInterruptConfig1   = 0x60,
            ExternalInterruptConfig2   = 0x64,
            ExternalInterruptConfig3   = 0x68,
            ExternalInterruptConfig4   = 0x6C,
            // 0x70-0x7C reserved
            InterruptMask1             = 0x80,
            EventMask1                 = 0x84,
            // 0x88-0x8C reserved
            InterruptMask2             = 0x90,
            EventMask2                 = 0x94,
        }
    }
}
