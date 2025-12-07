//
// Copyright (c) 2023-2025 OS Systems
//
//  This file is licensed under the MIT License.
//  Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Exceptions;

namespace Antmicro.Renode.Peripherals.Timers
{
    // This class does not implement advanced-control timers interrupts
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class STM32_Timer_FreqMeter : IDoubleWordPeripheral, IKnownSize
    {
        public STM32_Timer_FreqMeter(IMachine machine, uint frequency)
        {
            this.machine = machine;
            IRQ = new GPIO();
            Frequency = frequency;
            timerCounterLengthInBits = 16;

            var registersMap = new Dictionary<long, DoubleWordRegister>
            {
                {(long)Registers.Control1, new DoubleWordRegister(this)
                    .WithFlag(0, writeCallback: (_, val) =>
                    {
                        enableRequested = val;
                    }, valueProviderCallback: _ => enableRequested, name: "Counter enable (CEN)")
                    .WithFlag(1, out updateDisable, name: "Update disable (UDIS)")
                    .WithFlag(2, out updateRequestSource, name: "Update request source (URS)")
                    .WithTag("One-pulse mode (OPM)", 3, 1)
                    .WithTag("Direction (DIR)", 4, 1)
                    .WithEnumField(5, 2, out centerAlignedMode, name: "Center-aligned mode selection (CMS)")
                    .WithFlag(7, out autoReloadPreloadEnable, name: "Auto-reload preload enable (APRE)")
                    .WithTag("Clock Division (CKD)", 8, 2)
                    .WithReservedBits(10, 22)
                    .WithWriteCallback((_, __) => { UpdateInterrupts(); })
                },

                {(long)Registers.Control2, new DoubleWordRegister(this)
                    .WithTaggedFlag("CCPC", 0)
                    .WithReservedBits(1, 1)
                    .WithTaggedFlag("CCUS", 2)
                    .WithTaggedFlag("CCDS", 3)
                    .WithTag("MMS", 4, 2)
                    .WithTaggedFlag("TI1S", 7)
                    .WithTaggedFlag("OIS1", 8)
                    .WithTaggedFlag("OIS1N", 9)
                    .WithTaggedFlag("OIS2", 10)
                    .WithTaggedFlag("OIS2N", 11)
                    .WithTaggedFlag("OIS3", 12)
                    .WithTaggedFlag("OIS3N", 13)
                    .WithTaggedFlag("OIS4", 14)
                    .WithReservedBits(15, 17)
                },

                {(long)Registers.SlaveModeControl, new DoubleWordRegister(this)
                    .WithTag("SMS", 0, 3)
                    .WithTaggedFlag("OCCS", 3)
                    .WithTag("TS", 4, 2)
                    .WithTaggedFlag("MSM", 7)
                    .WithTag("ETF", 8, 3)
                    .WithTag("ETPS", 12, 2)
                    .WithTaggedFlag("ECE", 14)
                    .WithTaggedFlag("ETP", 15)
                    .WithReservedBits(16, 16)
                },

                {(long)Registers.DmaOrInterruptEnable, new DoubleWordRegister(this)
                    .WithFlag(0, out updateInterruptEnable, name: "Update interrupt enable (UIE)")
                    .WithFlag(1, valueProviderCallback: _ => ccInterruptEnable[0], writeCallback: (_, val) => WriteCaptureCompareInterruptEnable(0, val), name: "Capture/Compare 1 interrupt enable (CC1IE)")
                    .WithFlag(2, valueProviderCallback: _ => ccInterruptEnable[1], writeCallback: (_, val) => WriteCaptureCompareInterruptEnable(1, val), name: "Capture/Compare 2 interrupt enable (CC2IE)")
                    .WithFlag(3, valueProviderCallback: _ => ccInterruptEnable[2], writeCallback: (_, val) => WriteCaptureCompareInterruptEnable(2, val), name: "Capture/Compare 3 interrupt enable (CC3IE)")
                    .WithFlag(4, valueProviderCallback: _ => ccInterruptEnable[3], writeCallback: (_, val) => WriteCaptureCompareInterruptEnable(3, val), name: "Capture/Compare 4 interrupt enable (CC4IE)")
                    .WithReservedBits(5, 1)
                    .WithTag("Trigger interrupt enable (TIE)", 6, 1)
                    .WithReservedBits(7, 1)
                    .WithTag("Update DMA request enable (UDE)", 8, 1)
                    .WithTag("Capture/Compare 1 DMA request enable (CC1DE)", 9, 1)
                    .WithTag("Capture/Compare 2 DMA request enable (CC2DE)", 10, 1)
                    .WithTag("Capture/Compare 3 DMA request enable (CC3DE)", 11, 1)
                    .WithTag("Capture/Compare 4 DMA request enable (CC4DE)", 12, 1)
                    .WithReservedBits(13, 1)
                    .WithTag("Trigger DMA request enable (TDE)", 14, 1)
                    .WithReservedBits(15, 17)
                    .WithWriteCallback((_, __) => UpdateInterrupts())
                },

                {(long)Registers.Status, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Read | FieldMode.WriteZeroToClear,
                        writeCallback: (_, val) =>
                        {
                            if(!val)
                            {
                                updateInterruptFlag = false;
                                this.Log(LogLevel.Debug, "IRQ claimed");
                            }
                        },
                        valueProviderCallback: (_) =>
                        {
                            return updateInterruptFlag;
                        },
                        name: "Update interrupt flag (UIF)")
                    .WithFlag(1, FieldMode.Read | FieldMode.WriteZeroToClear, writeCallback: (_, val) => ClaimCaptureCompareInterrupt(0, val), valueProviderCallback: _ => ccInterruptFlag[0], name: "Capture/Compare 1 interrupt flag (CC1IF)")
                    .WithFlag(2, FieldMode.Read | FieldMode.WriteZeroToClear, writeCallback: (_, val) => ClaimCaptureCompareInterrupt(1, val), valueProviderCallback: _ => ccInterruptFlag[1], name: "Capture/Compare 2 interrupt flag (CC2IF)")
                    .WithFlag(3, FieldMode.Read | FieldMode.WriteZeroToClear, writeCallback: (_, val) => ClaimCaptureCompareInterrupt(2, val), valueProviderCallback: _ => ccInterruptFlag[2], name: "Capture/Compare 3 interrupt flag (CC3IF)")
                    .WithFlag(4, FieldMode.Read | FieldMode.WriteZeroToClear, writeCallback: (_, val) => ClaimCaptureCompareInterrupt(3, val), valueProviderCallback: _ => ccInterruptFlag[3], name: "Capture/Compare 4 interrupt flag (CC4IF)")
                    // Reserved fields were changed to flags to prevent from very frequent logging
                    .WithFlag(5, name: "Reserved1")
                    // These write callbacks are here only to prevent from very frequent logging.
                    .WithValueField(6, 1, FieldMode.WriteZeroToClear, writeCallback: (_, __) => {}, name: "Trigger interrupt flag (TIE)")
                    .WithFlag(7, name: "Reserved2")
                    .WithFlag(8, name: "Reserved3")
                    .WithValueField(9, 1, FieldMode.WriteZeroToClear, writeCallback: (_, __) => {}, name: "Capture/Compare 1 overcapture flag (CC1OF)")
                    .WithValueField(10, 1, FieldMode.WriteZeroToClear, writeCallback: (_, __) => {}, name: "Capture/Compare 2 overcapture flag (CC2OF)")
                    .WithValueField(11, 1, FieldMode.WriteZeroToClear, writeCallback: (_, __) => {}, name: "Capture/Compare 3 overcapture flag (CC3OF)")
                    .WithValueField(12, 1, FieldMode.WriteZeroToClear, writeCallback: (_, __) => {}, name: "Capture/Compare 4 overcapture flag (CC4OF)")
                    .WithValueField(13, 19, name: "Reserved4")
                    .WithWriteCallback((_, __) => UpdateInterrupts())
                },

                {(long)Registers.EventGeneration, new DoubleWordRegister(this)
                    .WithTag("Update generation (UG)", 0, 1)
                    .WithTag("Capture/compare 1 generation (CC1G)", 1, 1)
                    .WithTag("Capture/compare 2 generation (CC2G)", 2, 1)
                    .WithTag("Capture/compare 3 generation (CC3G)", 3, 1)
                    .WithTag("Capture/compare 4 generation (CC4G)", 4, 1)
                    .WithTaggedFlag("Capture/compare update generation (COMG)", 5)
                    .WithTag("Trigger generation (TG)", 6, 1)
                    .WithReservedBits(7, 25)
                    .WithWriteCallback((_, __) => UpdateInterrupts())
                },

                {(long)Registers.CaptureOrCompareMode1, new DoubleWordRegister(this)
                    // Fields of this register vary between 'Output compare'/'Input capture' mode
                    // Only fields for Intput capture mode are defined
                    .WithEnumField<DoubleWordRegister, CaptureCompareSelection>(0, 2, name: "CC1S")
                    .WithEnumField(2, 2, out inputCapturePrescaler[0], name: "IC1PSC")
                    .WithEnumField(4, 4, out inputCaptureFilter[0], name: "IC1F")
                    .WithEnumField<DoubleWordRegister, CaptureCompareSelection>(8, 2, name: "CC2S")
                    .WithEnumField(10, 2, out inputCapturePrescaler[0], name: "IC2PSC")
                    .WithEnumField(12, 4, out inputCaptureFilter[0], name: "IC2F")
                    .WithReservedBits(16, 16)
                },

                {(long)Registers.CaptureOrCompareMode2, new DoubleWordRegister(this)
                    // Fields of this register vary between 'Output compare'/'Input capture' mode
                    // Only fields for Intput capture mode are defined
                    .WithEnumField<DoubleWordRegister, CaptureCompareSelection>(0, 2, name: "CC3S")
                    .WithEnumField(2, 2, out inputCapturePrescaler[0], name: "IC3PSC")
                    .WithEnumField(4, 4, out inputCaptureFilter[0], name: "IC3F")
                    .WithEnumField<DoubleWordRegister, CaptureCompareSelection>(8, 2, name: "CC4S")
                    .WithEnumField(10, 2, out inputCapturePrescaler[0], name: "IC4PSC")
                    .WithEnumField(12, 4, out inputCaptureFilter[0], name: "IC4F")
                    .WithReservedBits(16, 16)
                },

                {(long)Registers.CaptureOrCompareEnable, new DoubleWordRegister(this)
                    .WithFlag(0, valueProviderCallback: _ => ccInputEnable[0], name: "Capture/Compare 1 enable (CC1E)")
                    .WithFlag(1, valueProviderCallback: _ => ccCapturePolarity[0], name: "Capture/Compare 1 polarity (CC1P)")
                    .WithTaggedFlag("CC1NE", 2)
                    .WithFlag(3, valueProviderCallback: _ => ccCaptureNPolarity[0], name: "Capture/Compare 1 Npolarity (CC1NP)")
                    .WithFlag(4, valueProviderCallback: _ => ccInputEnable[1], name: "Capture/Compare 2 enable (CC2E)")
                    .WithFlag(5, valueProviderCallback: _ => ccCapturePolarity[1], name: "Capture/Compare 2 polarity (CC2P)")
                    .WithTaggedFlag("CC2NE", 6)
                    .WithFlag(7, valueProviderCallback: _ => ccCaptureNPolarity[1], name: "Capture/Compare 2 Npolarity (CC2NP)")
                    .WithFlag(8, valueProviderCallback: _ => ccInputEnable[2], name: "Capture/Compare 3 enable (CC3E)")
                    .WithFlag(9, valueProviderCallback: _ => ccCapturePolarity[2], name: "Capture/Compare 3 polarity (CC3P)")
                    .WithTaggedFlag("CC3NE", 10)
                    .WithFlag(11, valueProviderCallback: _ => ccCaptureNPolarity[2], name: "Capture/Compare 3 Npolarity (CC3NP)")
                    .WithFlag(12, valueProviderCallback: _ => ccInputEnable[3], name: "Capture/Compare 4 enable (CC4E)")
                    .WithFlag(13, valueProviderCallback: _ => ccCapturePolarity[3], name: "Capture/Compare 4 polarity (CC4P)")
                    .WithFlag(15, valueProviderCallback: _ => ccCaptureNPolarity[3], name: "Capture/Compare 4 Npolarity (CC4NP)")
                    .WithReservedBits(16, 16)
                },

                {(long)Registers.Counter, new DoubleWordRegister(this)
                    .WithValueField(0, timerCounterLengthInBits, name: "Counter value (CNT)")
                    .WithReservedBits(timerCounterLengthInBits, 32 - timerCounterLengthInBits)
                    .WithWriteCallback((_, val) =>
                    {
                        UpdateInterrupts();
                    })
                },

                {(long)Registers.Prescaler, new DoubleWordRegister(this)
                    .WithValueField(0, 16, out prescaler, name: "Prescaler value (PSC)")
                    .WithReservedBits(16, 16)
                    .WithWriteCallback((_, __) =>
                    {
                        UpdateInterrupts();
                    })
                },

                {(long)Registers.AutoReload, new DoubleWordRegister(this)
                    .WithTaggedFlag("Auto-reload value (ARR)", 0)
                    .WithReservedBits(timerCounterLengthInBits, 32 - timerCounterLengthInBits)
                },
                {(long)Registers.RepetitionCounter, new DoubleWordRegister(this)
                    .WithValueField(0, 8, out repetitionCounter, name: "Repetition counter (TIM1_RCR)")
                    .WithReservedBits(8, 24)
                },
                {(long)Registers.BreakAndDeadTime, new DoubleWordRegister(this)
                    .WithTag("Dead Time Generator (DTG)", 0, 8)
                    .WithTag("LOCK", 8, 2)
                    .WithTaggedFlag("Off-state selection idle mode (OSSI)", 10)
                    .WithTaggedFlag("Off-state selection run mode (OSSR)", 11)
                    .WithTaggedFlag("Break enable (BKE)", 12)
                    .WithTaggedFlag("Break polarity (BKP)", 13)
                    .WithTaggedFlag("Automatic output enable (AOE)", 14)
                    .WithTaggedFlag("Main Output Enable (MOE)", 15)
                    .WithReservedBits(16, 16)
                },
            };

            for(var i = 0; i < NumberOfCCChannels; ++i)
            {
                var j = i;
                registersMap.Add((long)Registers.CaptureOrCompare1 + (j * 0x4), new DoubleWordRegister(this)
                    .WithValueField(0, timerCounterLengthInBits, valueProviderCallback: _ => (uint)ccTimers[j], writeCallback: (_, val) =>
                    {
                        ccTimers[j] = (uint)val;
                        ccInterruptFlag[j] = true;
                        this.Log(LogLevel.Debug, "cctimer{0}: Write {1}", j + 1, (uint)val);
                    }, name: String.Format("Capture/compare value {0} (CCR{0})", j + 1))
                    .WithReservedBits(timerCounterLengthInBits, 32 - timerCounterLengthInBits)
                    .WithWriteCallback((_, __) => { UpdateInterrupts(); })
                );
            }

            registers = new DoubleWordRegisterCollection(this, registersMap);
            Reset();

        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            registers.Write(offset, value);
        }

        public void Reset()
        {
            registers.Reset();
            enableRequested = false;
            updateInterruptFlag = false;
            for(var i = 0; i < NumberOfCCChannels; ++i)
            {
                ccTimers[i] = 0;
                ccInterruptFlag[i] = false;
                ccInterruptEnable[i] = false;
                ccInputEnable[i] = false;
            }
            UpdateInterrupts();
        }

        public GPIO IRQ { get; private set; }

        public long Size => 0x400;

        public void Register(IGPIOReceiver peripheral, NumberRegistrationPoint<int> registrationPoint)
        {
            machine.RegisterAsAChildOf(this, peripheral, registrationPoint);
        }

        public void Register(IGPIOReceiver peripheral, NullRegistrationPoint registrationPoint)
        {
            machine.RegisterAsAChildOf(this, peripheral, registrationPoint);
        }

        public void Unregister(IGPIOReceiver peripheral)
        {
            machine.UnregisterAsAChildOf(this, peripheral);
        }

        public void InputCapturePeriodically(int i, uint value, uint frequency)
        {
            Action feedSample = () =>
            {
                uint ICxPolarity;

                if(ccCapturePolarity[i] && ccCaptureNPolarity[i]){
                    ICxPolarity = 2;
                }
                else {
                    ICxPolarity = 1;
                }

                uint val = (uint)inputCapturePrescaler[0].Value;
                uint ICxPrescaler = (uint) Math.Pow(2, val);
                uint PSC = (uint)prescaler.Value;
                uint uwDiffCapture = (uint)((Frequency * ICxPrescaler) / (value * PSC * ICxPolarity)+1);
                WriteDoubleWord((long)Registers.CaptureOrCompare1 + (i * 0x4), uwDiffCapture);
                return;
            };

            Func<bool> stopCondition = () =>
            {
                return false;
            };

            var feederThread = machine.ObtainManagedThread(feedSample, frequency, "Freq Meter", this.machine, stopCondition);
            feederThread.Start();
        }

        private void WriteCaptureCompareOutputPolarity(int i, bool value)
        {
            ccCapturePolarity[i] = value;
        }
        private void WriteCaptureCompareOutputNPolarity(int i, bool value)
        {
            ccCaptureNPolarity[i] = value;
        }

        private void WriteCaptureCompareInterruptEnable(int i, bool value)
        {
            ccInterruptEnable[i] = value;
            this.Log(LogLevel.Debug, "cctimer{0}: Interrupt Enable set to {1}", i + 1, value);
        }

        private void ClaimCaptureCompareInterrupt(int i, bool value)
        {
            this.Log(LogLevel.Debug, "cctimer{0}: Compare IRQ claimed with value {1} and Flag {2}", i + 1, value, ccInterruptFlag[i]);
            if (ccInterruptFlag[i])
            {
                ccInterruptFlag[i] = false;
            }
        }

        private void UpdateInterrupts()
        {
            for(var i = 0; i < NumberOfCCChannels; ++i)
            {
                this.Log(LogLevel.Debug, "UpdateInterrupts {0}, {1}", ccInterruptFlag[i], ccTimers[i]);

                IRQ.Set(ccInterruptFlag[i]);
            }
        }

        private readonly int timerCounterLengthInBits;
        private readonly ulong Frequency;
        private bool updateInterruptFlag;
        private bool enableRequested;
        private uint[] ccTimers = new uint[NumberOfCCChannels];
        private bool[] ccInterruptFlag = new bool[NumberOfCCChannels];
        private bool[] ccInterruptEnable = new bool[NumberOfCCChannels];
        private bool[] ccInputEnable = new bool[NumberOfCCChannels];
        private bool[] ccCapturePolarity = new bool[NumberOfCCChannels];
        private bool[] ccCaptureNPolarity = new bool[NumberOfCCChannels];
        private readonly IFlagRegisterField updateDisable;
        private readonly IFlagRegisterField updateRequestSource;
        private readonly IFlagRegisterField updateInterruptEnable;
        private readonly IFlagRegisterField autoReloadPreloadEnable;
        private readonly IEnumRegisterField<CenterAlignedMode> centerAlignedMode;
        private readonly IValueRegisterField repetitionCounter;
        private readonly IValueRegisterField prescaler;
        private readonly DoubleWordRegisterCollection registers;
        private readonly IEnumRegisterField<CaptureCompareSelection>[] modes = new IEnumRegisterField<CaptureCompareSelection>[NumberOfCCChannels];
        private readonly IEnumRegisterField<InputCapturePrescaler>[] inputCapturePrescaler = new IEnumRegisterField<InputCapturePrescaler>[NumberOfCCChannels];
        private readonly IEnumRegisterField<InputCaptureFilter>[] inputCaptureFilter = new IEnumRegisterField<InputCaptureFilter>[NumberOfCCChannels];
        private readonly IMachine machine;

        private const int NumberOfCCChannels = 4;

        private enum CenterAlignedMode
        {
            EdgeAligned    = 0,   // Direction depending on direction bit (TIMx_CR1::BIT)
            CenterAligned1 = 1,   // Up and down alternatively, compare interrupt flag set only when counting down
            CenterAligned2 = 2,   // Up and down alternatively, compare interrupt flag set only when counting up
            CenterAligned3 = 3,   // Up and down alternatively, compare interrupt flag set on both up/down counting
        }

        private enum CaptureCompareSelection
        {
            Output   = 0, // Channel is configured as an output
            InputTi1 = 1, // Channel is configured as an input, mapped on TI1
            InputTi2 = 2, // Channel is configured as an input, mapped on TI2
            InputTrc = 3, // Channel is configured as an input, mapped on TRC
        }

        private enum InputCapturePrescaler
        {
            NoPrescaler  = 0, //no prescaler, capture is done each time an edge is detected on the capture input
            Every2Events = 1, //capture is done once every 2 events
            Every4Events = 2, //capture is done once every 4 events
            Every8Events = 3, //capture is done once every 8 events
        }

        private enum InputCaptureFilter
        {
            //The digital filter is made of an event counter in which N consecutive events are needed to
            //validate a transition on the output
            NoFilter    = 0000, //sampling is done at fDTS
            fCK_INT_N2  = 0001, // fSAMPLING=fCK_INT, N=2
            fCK_INT_N4  = 0010, //fSAMPLING=fCK_INT, N=4
            fCK_INT_N8  = 0011, //fSAMPLING=fCK_INT, N=8
            fDTS_2_N6   = 0100, //fSAMPLING=fDTS/2, N=6
            fDTS_2_N8   = 0101, //fSAMPLING=fDTS/2, N=8
            fDTS_4_N6   = 0110, //fSAMPLING=fDTS/4, N=6
            fDTS_4_N8   = 0111, //fSAMPLING=fDTS/4, N=8
            fDTS_8_N6   = 1000, //fSAMPLING=fDTS/8, N=6
            fDTS_8_N8   = 1001, //fSAMPLING=fDTS/8, N=8
            fDTS_16_N5  = 1010, //fSAMPLING=fDTS/16, N=5
            fDTS_16_N6  = 1011, //fSAMPLING=fDTS/16, N=6
            fDTS_16_N8  = 1100, //fSAMPLING=fDTS/16, N=8
            fDTS_32_N5  = 1101, //fSAMPLING=fDTS/32, N=5
            fDTS_32_N6  = 1110, //fSAMPLING=fDTS/32, N=6
            fDTS_32_N8  = 1111, //fSAMPLING=fDTS/32, N=8
        }

        private enum Registers : long
        {
            Control1 = 0x0,
            Control2 = 0x04,
            SlaveModeControl = 0x08,
            DmaOrInterruptEnable = 0x0C,
            Status = 0x10,
            EventGeneration = 0x14,
            CaptureOrCompareMode1 = 0x18,
            CaptureOrCompareMode2 = 0x1C,
            CaptureOrCompareEnable = 0x20,
            Counter = 0x24,
            Prescaler = 0x28,
            AutoReload = 0x2C,
            // gap intended
            RepetitionCounter = 0x30,
            // gap intended
            CaptureOrCompare1 = 0x34,
            CaptureOrCompare2 = 0x38,
            CaptureOrCompare3 = 0x3C,
            CaptureOrCompare4 = 0x40,
            BreakAndDeadTime = 0x44,
            // gap intended
            DmaControl = 0x48,
            DmaAddressForFullTransfer = 0x4C,
            Option = 0x50
        }
    }
}
