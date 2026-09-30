//
// Copyright (c) 2010-2026 Antmicro
// Copyright (c) 2023-2026 OS Systems
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.Sensor;
using Antmicro.Renode.Utilities.RESD;

namespace Antmicro.Renode.Peripherals.Analog
{
    // Superset of all ADC features found on many STM MPU series.
    //
    // Available features:
    //     adcVersion --------- Specifies from the AdcVersion enum which register layout this ADC uses.
    //     watchdogCount ------ Specifies the number of analog watchdogs inside the peripheral between 1 and 3.
    //    *hasCalibration ----- Specifies whether the calibration factor is available to the software.
    //                          ADCs without this feature will still have the ADCAL flag available to trigger the calibration procedure,
    //                          but not the CALFACT register.
    //     voltageRegulator --- Specifies from the VoltageRegulator enum how the ADVREGEN field is defined.
    //    *hasDeepPowerDown --- Specifies whether this ADC has the DEEPPWD bit. The bit is tagged but its value is not used by the model.
    //    *hasLowFrequencyMode  Specifies whether this ADC has the LFMEN bit. The bit is tagged but its value is not used by the model.
    //    *hasOversampler ----- Specifies whether this ADC has the V1/V4 oversampler bits in ADC_CFGR2. These bits are stored but not used by the model.
    //    *hasLowFrequencyTrigger Specifies whether this ADC has the LFTRIG bit in ADC_CFGR2. The bit is stored but not used by the model.
    //     channelCount ------- Specifies the amount of available channels.
    //                          Includes both internal sources (like the temperature sensor) as well as external.
    //    *hasPrescaler ------- Specifies whether the ADC contains a prescaler for the external clock input.
    //                          Technically either this property could be made an enum,
    //                          or there could be added a separate property which describes whether the internal clock can be used.
    //                          ex.
    //                            - the STM32F0xx can either use PCLK or the ADC asynchronous clock and has no precaler
    //                            - the STM32WBA only uses the ADC asynchronous clock but has a precaler
    //                          but for now, this feature describes both (i.e. true means has prescaler *and* no internal clock).
    //    *hasVbatPin --------- Specifies whether this ADC provides a pin for monitoring of an external power supply.
    //    *hasChannelSequence - Specifies whether this ADC provides a fully configurable sequencer.
    //                          If not, the ADC can convert a single channel or a sequence of channels,
    //                          but only scanning sequentially either forwards or backwards.
    //    *hasOffset ---------- Specifies whether this ADC has offset registers. These registers are tagged but not used by the model.
    //    *hasDifferentialMode  Specifies whether this has differential mode. The differential mode register is tagged but its
    //                          value is not used by the model.
    //    *samplingTime ------- Specifies from the SamplingTime enum how the sampling time registers are defined. These registers
    //                          are tagged but their value are not used by the model.
    //    *dualMode ----------- Indicates if there is a secondary ADC that can work in dual mode.
    //    hasLinearityCalibration - Specifies whether the ADC supports linear calibration procedure.
    //    *injectedChannels --- Specifies whether injected channels are supported (auto-injection, external trigger, queuing, JQOVF not implemented).
    //    resolutionRange -- Specifies bit resolution range this peripheral supports.
    //    hasChannelPreselection - Specifies whether ADC requires preselecting channels to be sampled.
    //    hasScanDirection ---- Specifies whether the ADC supports descending sequence scanning.
    //
    // * - Feature is either partially implemented, or not at all.
    public abstract class STM32_ADC_Common : IKnownSize, IProvidesRegisterCollection<DoubleWordRegisterCollection>, IDoubleWordPeripheral, IWordPeripheral, IADC
    {
        public STM32_ADC_Common(IMachine machine, double referenceVoltage, uint externalEventFrequency, int dmaChannel, IDMA dmaPeripheral,
            AdcVersion adcVersion, int watchdogCount, bool hasCalibration, VoltageRegulator voltageRegulator, bool hasDeepPowerDown, bool hasLowFrequencyMode, bool hasOversampler, bool hasLowFrequencyTrigger, int channelCount, bool hasPrescaler,
            bool hasVbatPin, bool hasChannelSequence,
            bool hasOffset, bool hasDifferentialMode, SamplingTime samplingTime, bool dualMode, bool hasLinearityCalibration, bool hasChannelInjection, ResolutionRange resolutionRange, bool hasChannelPreselection, bool hasScanDirection)
        {
            if(dmaPeripheral == null)
            {
                if(dmaChannel != 0)
                {
                    throw new ConstructionException($"Unspecified DMA peripheral to use with channel number {dmaChannel}");
                }
            }
            else
            {
                if(dmaChannel <= 0 || dmaChannel > dmaPeripheral.NumberOfChannels)
                {
                    throw new ConstructionException($"Invalid 'dmaChannel' argument value: '{dmaChannel}'. Available channels: 1-{dmaPeripheral.NumberOfChannels}");
                }
            }
            if(adcVersion == AdcVersion.V5)
            {
                throw new ConstructionException($"ADC version {adcVersion} is not supported yet");
            }

            this.machine = machine;
            ADCContainer = new SimpleContainerHelper<IRESDSampleSource<VoltageSample>>(machine, this);

            ADCChannelCount = channelCount;
            WatchdogCount = watchdogCount;
            this.adcVersion = adcVersion;
            hasChannelSelect = adcVersion == AdcVersion.V1 || adcVersion == AdcVersion.V4;
            hasSeparateThresholdRegisters = adcVersion == AdcVersion.V3 || adcVersion == AdcVersion.V5;
            this.hasChannelInjection = hasChannelInjection;
            this.resolutionRange = resolutionRange;
            this.hasChannelPreselection = hasChannelPreselection;

            if(WatchdogCount < 1 || WatchdogCount > 3)
            {
                throw new ConstructionException("Invalid watchdog count");
            }
            if(voltageRegulator == VoltageRegulator.TwoBit && hasDeepPowerDown)
            {
                throw new ConstructionException("Two bit ADVREGEN overlaps DEEPPWD");
            }
            if(hasOversampler && adcVersion != AdcVersion.V1 && adcVersion != AdcVersion.V4)
            {
                throw new ConstructionException($"Oversampler is not supported on {adcVersion}");
            }
            if(hasLowFrequencyTrigger && adcVersion != AdcVersion.V1 && adcVersion != AdcVersion.V4)
            {
                throw new ConstructionException($"LFTRIG is not supported on {adcVersion}");
            }
            this.voltageRegulator = voltageRegulator;
            hasEndOfCalibration = hasCalibration && (adcVersion == AdcVersion.V1 || adcVersion == AdcVersion.V4);

            registers = new DoubleWordRegisterCollection(this, BuildRegistersMap(hasCalibration,
                                                                                 hasDeepPowerDown,
                                                                                 hasLowFrequencyMode,
                                                                                 hasOversampler,
                                                                                 hasLowFrequencyTrigger,
                                                                                 hasPrescaler,
                                                                                 hasVbatPin,
                                                                                 hasChannelSequence,
                                                                                 hasOffset,
                                                                                 hasDifferentialMode,
                                                                                 samplingTime,
                                                                                 dualMode,
                                                                                 hasLinearityCalibration,
                                                                                 hasChannelInjection,
                                                                                 hasScanDirection));

            IRQ = new GPIO();
            this.dmaChannel = dmaChannel;
            this.dma = dmaPeripheral;
            this.referenceVoltage = referenceVoltage;
            this.externalEventFrequency = externalEventFrequency;

            samplingThread = machine.ObtainManagedThread(StartSampling, externalEventFrequency);
            channelSelected = new bool[ADCChannelCount];
            Reset();

            this.RegisterDefaultChildren(machine);
        }

        public void Reset()
        {
            RegistersCollection.Reset();
            for(var i = 0; i < ADCChannelCount; i++)
            {
                channelSelected[i] = false;
            }
            currentChannel = 0;
            awaitingConversion = false;
            enabled = false;
            externalTrigger = false;
            sequenceInProgress = false;
            sequenceCounter = 0;
            samplingThread.Stop();
        }

        public uint ReadDoubleWord(long offset)
        {
            return RegistersCollection.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            RegistersCollection.Write(offset, value);
        }

        public ushort ReadWord(long offset)
        {
            return (ushort)RegistersCollection.Read(offset);
        }

        public void WriteWord(long offset, ushort value)
        {
            RegistersCollection.Write(offset, value);
        }

        public DoubleWordRegisterCollection RegistersCollection { get => registers; }

        public long Size => 0x400;

        public GPIO IRQ { get; }

        public int ADCChannelCount { get; }

        public SimpleContainerHelper<IRESDSampleSource<VoltageSample>> ADCContainer { get; }

        private void WarnOnTooBigValue(int channel, double mv)
        {
            var maxValue = (1 << data.Width) - 1;

            if(MillivoltsToSample(mv, MinResolutionBits) > maxValue)
            {
                this.Log(LogLevel.Warning, "Channel {0}: {1}mV is too big in any ADC configuration", channel, mv);
            }
            else if(MillivoltsToSample(mv) > maxValue)
            {
                this.Log(LogLevel.Warning, "Channel {0}: {1}mV is too big for current ADC resolution", channel, mv);
            }
            else if(MillivoltsToSample(mv, MaxResolutionBits) > maxValue)
            {
                this.Log(LogLevel.Debug,
                         "Channel {0}: {1}mV will be too big for some ADC resolution other than the current one",
                         channel, mv);
            }
        }

        private void WarnOnCurrentChannelNotPreselected()
        {
            if(hasChannelPreselection && !preselectedChannels[currentChannel].Value)
            {
                this.Log(LogLevel.Warning, "Channel {0} is not preselected", currentChannel);
            }
        }

        private void UpdateInterrupts()
        {
            var irq = false;

            irq |= adcReadyFlag.Value && adcReadyInterruptEnable.Value;
            irq |= analogWatchdogsInterruptEnable.Zip(analogWatchdogFlags, (enable, flag) =>
            {
                return enable.Value && flag.Value;
            }).Any(flag => flag);
            irq |= endOfSamplingFlag.Value && endOfSamplingInterruptEnable.Value;
            irq |= endOfConversionFlag.Value && endOfConversionInterruptEnable.Value;
            irq |= endOfSequenceFlag.Value && endOfSequenceInterruptEnable.Value;
            irq |= adcOverrunFlag.Value && adcOverrunInterruptEnable.Value;
            if(hasChannelInjection)
            {
                irq |= endOfConversionInjectedFlag.Value && endOfConversionInjectedInterruptEnable.Value;
                irq |= endOfSequenceInjectedFlag.Value && endOfSequenceInjectedInterruptEnable.Value;
            }
            if(hasEndOfCalibration)
            {
                irq |= endOfCalibrationFlag.Value && endOfCalibrationInterruptEnable.Value;
            }
            IRQ.Set(irq);
        }

        private void StartSampling()
        {
            if(sequenceInProgress)
            {
                if(waitFlag.Value)
                {
                    awaitingConversion = true;
                    return;
                }
                this.Log(LogLevel.Warning, "Issued a start event before the last sequence finished");
                return;
            }

            sequenceInProgress = true;
            startFlag.Value = true;

            if(hasChannelSelect)
            {
                // NOTE: We set current channel out of bounds to switch to first active channel
                currentChannel = (scanDirection == ScanDirection.Ascending) ? -1 : ADCChannelCount;
                SwitchToNextActiveChannel();
            }
            else
            {
                sequenceCounter = (scanDirection == ScanDirection.Ascending) ? 0 : (int)regularSequenceLength.Value;
                currentChannel = (int)regularSequence[sequenceCounter].Value;
            }
            SampleNextChannel();
        }

        private void StartInjectedSampling()
        {
            injectedSequenceCounter = 0;
            SampleNextInjectedChannel();
        }

        private void SendDmaRequest()
        {
            if(dma != null)
            {
                dma.RequestTransfer(dmaChannel);
            }
            else
            {
                this.Log(LogLevel.Warning, "Received DMA transfer request, but no DMA is configured for this peripheral.");
            }
        }

        private bool WatchdogEnabled(int watchdogNumber)
        {
            switch(watchdogNumber)
            {
            case 0:
                var enabledOnAll = !analogWatchdogSingleChannel.Value;
                var enabledOnCurrent = enabledOnAll || (int)analogWatchdogChannel.Value == currentChannel;
                return analogWatchdogEnable.Value && enabledOnCurrent;
            default:
                return analogWatchdogSelectedChannels[watchdogNumber][currentChannel].Value;
            }
        }

        private ulong ClampSample(uint sample, int width)
        {
            if(sample < (1u << width))
            {
                return sample;
            }
            else
            {
                var clampedSample = (ulong)(1 << width) - 1;
                this.Log(LogLevel.Warning, "Sample value {0} is too big for ADC data register, clamping it to {1}",
                         sample, clampedSample);
                return clampedSample;
            }
        }

        private void SampleNextChannel()
        {
            // Exit when peripheral is not enabled
            if(!enabled)
            {
                currentChannel = 0;
                sequenceCounter = 0;
                sequenceInProgress = false;
                return;
            }

            if(sequenceInProgress)
            {
                uint sample = GetSampleFromChannel(currentChannel);
                WarnOnTooBigValue(currentChannel, (double)sample / 1e3); // µV to mV
                if(!adcOverrunFlag.Value || overrunMode.Value)
                {
                    data.Value = ClampSample(sample, data.Width);
                }
                endOfSamplingFlag.Value = true;

                for(int i = 0; i < WatchdogCount; i++)
                {
                    if(WatchdogEnabled(i))
                    {
                        if(sample > analogWatchdogHighValues[i].Value || sample < analogWatchdogLowValues[i].Value)
                        {
                            analogWatchdogFlags[i].Value = true;
                            this.Log(LogLevel.Debug, "Analog watchdog {0} flag raised for value {1} on channel {2}", i, data.Value, currentChannel);
                        }
                    }
                }
                if(endOfConversionFlag.Value)
                {
                    adcOverrunFlag.Value = true;
                }
                endOfConversionFlag.Value = true;
                this.Log(LogLevel.Debug, "Sampled channel {0}", currentChannel);
                if(dmaEnabled.Value && !adcOverrunFlag.Value)
                {
                    SendDmaRequest();
                }
                SwitchToNextActiveChannel();
            }

            if(!sequenceInProgress && awaitingConversion)
            {
                awaitingConversion = false;
                StartSampling();
            }
            UpdateInterrupts();
        }

        private void SampleNextInjectedChannel()
        {
            // Exit when peripheral is not enabled
            if(!enabled)
            {
                injectedSequenceCounter = 0;
                return;
            }

            Func<bool> iterationFinished = () => injectedSequenceCounter > (int)injectedSequenceLength.Value;

            if(!iterationFinished())
            {
                int currentInjectedChannel = (int)injectedSequence[injectedSequenceCounter].Value;

                uint sample = GetSampleFromChannel(currentInjectedChannel);
                if(!adcOverrunFlag.Value || overrunMode.Value)
                {
                    var register = injectedData[injectedSequenceCounter];
                    register.Value = ClampSample(sample, register.Width);
                }

                endOfConversionInjectedFlag.Value = true;
                this.Log(LogLevel.Debug, "Sampled injected channel {0}", currentInjectedChannel);
                injectedSequenceCounter++;
            }

            if(iterationFinished())
            {
                this.Log(LogLevel.Debug, "No more injected channels enabled");
                startInjectionFlag.Value = false;
                endOfSequenceInjectedFlag.Value = true;
            }
            UpdateInterrupts();
        }

        private void SwitchToNextActiveChannel()
        {
            if(!sequenceInProgress)
            {
                WarnOnCurrentChannelNotPreselected();
                return;
            }

            var iterationFinished = false;
            do
            {
                iterationFinished = SwitchToNextChannel();
            }
            while(!iterationFinished && hasChannelSelect && !channelSelected[currentChannel]);

            if(iterationFinished)
            {
                this.Log(LogLevel.Debug, "No more channels enabled");
                endOfSequenceFlag.Value = true;
                sequenceCounter = 0;
                startFlag.Value = false;
                sequenceInProgress = false;
            }
        }

        private bool SwitchToNextChannel()
        {
            if(hasChannelSelect)
            {
                currentChannel = (scanDirection == ScanDirection.Ascending) ? currentChannel + 1 : currentChannel - 1;
                return currentChannel >= ADCChannelCount || currentChannel < 0;
            }
            else
            {
                sequenceCounter = (scanDirection == ScanDirection.Ascending) ? sequenceCounter + 1 : sequenceCounter - 1;
                if(sequenceCounter >= 0 && sequenceCounter <= (int)regularSequenceLength.Value)
                {
                    currentChannel = (int)regularSequence[sequenceCounter].Value;
                }
                return sequenceCounter > (int)regularSequenceLength.Value || sequenceCounter < 0 || currentChannel < 0;
            }
        }

        private uint GetSampleFromChannel(int channelNumber)
        {
            IRESDSampleSource<VoltageSample> sampleSource;
            var milliVolts = 0.0;

            if(ADCContainer.TryGetByAddress(channelNumber, out sampleSource))
            {
                milliVolts = sampleSource.Sample.Voltage / 1000;
            }
            WarnOnCurrentChannelNotPreselected();
            return MillivoltsToSample(milliVolts);
        }

        private uint MillivoltsToSample(double sampleInMillivolts)
        {
            return MillivoltsToSample(sampleInMillivolts, ResolutionToBits(resolution.Value));
        }

        private uint MillivoltsToSample(double sampleInMillivolts, ushort resolutionInBits)
        {
            uint referencedValue = (uint)Math.Round((sampleInMillivolts / (referenceVoltage * 1000)) * ((1 << resolutionInBits) - 1));
            if(align.Value == Align.Left)
            {
                referencedValue = referencedValue << (16 - resolutionInBits);
            }
            return referencedValue;
        }

        private Dictionary<long, DoubleWordRegister> BuildRegistersMap(bool hasCalibration, bool hasDeepPowerDown, bool hasLowFrequencyMode, bool hasOversampler, bool hasLowFrequencyTrigger, bool hasPrescaler, bool hasVbatPin, bool hasChannelSequence, bool hasOffset, bool hasDifferentialMode, SamplingTime samplingTime, bool dualMode, bool hasLinearityCalibration, bool hasChannelInjection, bool hasScanDirection)
        {
            var hasPowerRegister = adcVersion == AdcVersion.V4;

            var isrRegister = new DoubleWordRegister(this)
                .WithFlag(0, out adcReadyFlag, FieldMode.Read | FieldMode.WriteOneToClear, name: "ADRDY")
                .WithFlag(1, out endOfSamplingFlag, FieldMode.Read | FieldMode.WriteOneToClear, name: "EOSMP")
                .WithFlag(2, out endOfConversionFlag, FieldMode.Read | FieldMode.WriteOneToClear,  writeCallback: (_, val) =>
                    {
                        if(val && sequenceInProgress)
                        {
                            // Clearing the End Of Conversion flag triggers next conversion
                            // This function call must be delayed to avoid deadlock on registers access
                            machine.LocalTimeSource.ExecuteInNearestSyncedState((___) => SampleNextChannel());
                        }
                    }, name: "EOC")
                .WithFlag(3, out endOfSequenceFlag, FieldMode.Read | FieldMode.WriteOneToClear, name: "EOS")
                .WithFlag(4, out adcOverrunFlag, FieldMode.Read | FieldMode.WriteOneToClear, name: "OVR")
                .WithFlags(7, WatchdogCount, out analogWatchdogFlags, FieldMode.Read | FieldMode.WriteOneToClear, name: "AWD")
                .WithReservedBits(7 + WatchdogCount, 3 - WatchdogCount)
                .WithReservedBits(13, 19)
                .WithWriteCallback((_, __) => UpdateInterrupts());

            var interruptEnableRegister = new DoubleWordRegister(this)
                .WithFlag(0, out adcReadyInterruptEnable, name: "ADRDYIE")
                .WithFlag(1, out endOfSamplingInterruptEnable, name: "EOSMPIE")
                .WithFlag(2, out endOfConversionInterruptEnable, name: "EOCIE")
                .WithFlag(3, out endOfSequenceInterruptEnable, name: "EOSIE")
                .WithFlag(4, out adcOverrunInterruptEnable, name: "OVRIE")
                .WithFlags(7, WatchdogCount, out analogWatchdogsInterruptEnable, name: "AWDIE")
                .WithReservedBits(7 + WatchdogCount, 3 - WatchdogCount)
                .WithReservedBits(13, 19)
                .WithWriteCallback((_, __) => UpdateInterrupts());

            if(hasEndOfCalibration)
            {
                isrRegister
                    .WithFlag(11, out endOfCalibrationFlag, FieldMode.Read | FieldMode.WriteOneToClear, name: "EOCAL");
                interruptEnableRegister
                    .WithFlag(11, out endOfCalibrationInterruptEnable, name: "EOCALIE");
            }
            else
            {
                isrRegister
                    .WithReservedBits(11, 1);
                interruptEnableRegister
                    .WithReservedBits(11, 1);
            }

            if(voltageRegulator != VoltageRegulator.None && (adcVersion == AdcVersion.V3 || adcVersion == AdcVersion.V4 || adcVersion == AdcVersion.V5))
            {
                isrRegister
                    // Simplified logic - hardware delays LDORDY until voltage regulator settles.
                    .WithFlag(12, valueProviderCallback: _ => IsRegulatorEnabled(), name: "LDORDY");
                interruptEnableRegister
                    .WithTaggedFlag("LDORDYIE", 12);
            }
            else
            {
                isrRegister
                    .WithReservedBits(12, 1);
                interruptEnableRegister
                    .WithReservedBits(12, 1);
            }

            if(hasChannelInjection)
            {
                isrRegister
                    .WithFlag(5, out endOfConversionInjectedFlag, FieldMode.Read | FieldMode.WriteOneToClear, writeCallback: (_, val) =>
                        {
                            if(val && startInjectionFlag.Value)
                            {
                                machine.LocalTimeSource.ExecuteInNearestSyncedState((___) => SampleNextInjectedChannel());
                            }
                        },
                        name: "JEOC")
                    .WithFlag(6, out endOfSequenceInjectedFlag, FieldMode.Read | FieldMode.WriteOneToClear, name: "JEOS")
                    .WithTaggedFlag("JQOVF", 10);

                interruptEnableRegister
                    .WithFlag(5, out endOfConversionInjectedInterruptEnable, name: "JEOCIE")
                    .WithFlag(6, out endOfSequenceInjectedInterruptEnable, name: "JEOSIE")
                    .WithTaggedFlag("JQOVFIE", 10);
            }
            else
            {
                isrRegister
                    .WithReservedBits(5, 2)
                    .WithReservedBits(10, 1);
                interruptEnableRegister
                    .WithReservedBits(5, 2)
                    .WithReservedBits(10, 1);
            }

            // SCANDIR, or a reserved bit, takes the bit left free by the RES field
            var resolutionOffset = adcVersion == AdcVersion.V1 || adcVersion == AdcVersion.V2 ? 3 : 2;
            var scanDirectionOffset = resolutionOffset == 2 ? 4 : 2;
            var externalTriggerSelectionWidth = adcVersion == AdcVersion.V1 || adcVersion == AdcVersion.V4 ? 3 : 4;

            var configurationRegister1 = new DoubleWordRegister(this)
                .WithFlag(0, out dmaEnabled, name: "DMAEN")
                .WithFlag(1, writeCallback: (_, val) =>
                    {
                        if(!val && dmaEnabled.Value)
                        {
                            this.Log(LogLevel.Warning, "DMA One Shot mode not supported");
                        }
                    }, name: "DMACFG")
                .WithValueField(resolutionOffset, adcVersion == AdcVersion.V3 ? 3 : 2, out resolution, writeCallback: (_, val) =>
                    {
                        if(adcVersion == AdcVersion.V3 && val == 0b100)
                        {
                            this.Log(LogLevel.Warning, "RES 0b100 is the 8 bit resolution of revision Y devices, use 0b111");
                        }
                    }, name: "RES")
                .WithEnumField<DoubleWordRegister, Align>(5, 1, out align, name: "ALIGN")
                .WithTag("EXTSEL", 6, externalTriggerSelectionWidth)
                .WithValueField(10, 2, writeCallback: (_, val) =>
                    {
                        // On hardware it is possible to configure on which edge should the trigger fire
                        // This Peripheral mocks external trigger using `externalEventFrequency`, so we only distinguish between manual/external trigger
                        externalTrigger = (val > 0);
                    }, name: "EXTEN")
                .WithFlag(12, out overrunMode, name: "OVRMOD")
                .WithFlag(13, out continuous, writeCallback: (prevVal, val) =>
                    {
                        if(!val)
                        {
                            samplingThread.Stop();
                            sequenceInProgress = false;
                        }
                        else if(startFlag.Value && !prevVal)
                        {
                            this.Log(LogLevel.Warning, "Can set continuous mode only when ADSTART is 0");
                            continuous.Value = false;
                        }
                    }, name: "CONT")
                .WithFlag(14, out waitFlag, name: "WAIT")
                .WithTaggedFlag("DISCEN", 16)
                .WithTag("DISCNUM", 17, 3)
                .WithFlag(22, out analogWatchdogSingleChannel, name: "AWDSGL")
                .WithFlag(23, out analogWatchdogEnable, name: "AWDEN")
                .WithValueField(26, 5, out analogWatchdogChannel, name: "AWDCH");

            if(externalTriggerSelectionWidth == 3)
            {
                configurationRegister1
                    .WithReservedBits(9, 1);
            }

            if(hasChannelInjection)
            {
                configurationRegister1
                    .WithTaggedFlag("JDISCEN", 20)
                    .WithTaggedFlag("JQM", 21)
                    .WithTaggedFlag("JAWD1EN", 24)
                    .WithTaggedFlag("JAUTO", 25)
                    .WithTaggedFlag("JQDIS", 31);
            }
            else
            {
                configurationRegister1
                    .WithReservedBits(20, 1)
                    .WithReservedBits(24, 2)
                    .WithReservedBits(31, 1);
            }

            if(!hasPowerRegister)
            {
                configurationRegister1
                    .WithTaggedFlag("AUTOFF", 15);
            }
            else
            {
                configurationRegister1
                    .WithReservedBits(15, 1);
            }

            if(hasChannelSequence)
            {
                if(!hasChannelInjection)
                {
                    configurationRegister1
                        .WithFlag(21, name: "CHSELRMOD"); // no actual logic, but software expects to read the value back
                }
            }
            else
            {
                configurationRegister1
                    .WithReservedBits(21, 1);
            }

            if(hasScanDirection)
            {
                configurationRegister1
                    .WithEnumField<DoubleWordRegister, ScanDirection>(scanDirectionOffset, 1, writeCallback: (_, val) =>
                        {
                            scanDirection = val;
                        }, name: "SCANDIR");
            }
            else
            {
                scanDirection = ScanDirection.Ascending;
                if(adcVersion != AdcVersion.V3)
                {
                    configurationRegister1.WithReservedBits(scanDirectionOffset, 1);
                }
            }

            var configurationRegister2 = new DoubleWordRegister(this)
                .WithReservedBits(10, 19)
                .WithTag("CKMODE", 30, 2);

            if(hasLowFrequencyTrigger)
            {
                configurationRegister2
                    .WithFlag(29, name: "LFTRIG");
            }
            else
            {
                configurationRegister2
                    .WithReservedBits(29, 1);
            }

            if(hasOversampler)
            {
                configurationRegister2
                    .WithFlag(0, name: "OVSE")
                    .WithReservedBits(1, 1)
                    .WithValueField(2, 3, name: "OVSR")
                    .WithValueField(5, 4, name: "OVSS")
                    .WithFlag(9, name: "TOVS");
            }
            else
            {
                configurationRegister2
                    .WithReservedBits(0, 10);
            }

            var commonConfigurationRegister = new DoubleWordRegister(this)
                .WithReservedBits(0, 16)
                .WithValueField(16, 2, name: "CKMODE") // no actual logic, since we do not handle clock in this model
                .WithTaggedFlag("VREFEN", 22)
                .WithTaggedFlag("TSEN", 23)
                .WithReservedBits(26, 6);

            if(hasLowFrequencyMode)
            {
                commonConfigurationRegister
                    .WithTaggedFlag("LFMEN", 25);
            }
            else
            {
                commonConfigurationRegister
                    .WithReservedBits(25, 1);
            }

            if(hasPrescaler)
            {
                commonConfigurationRegister
                    .WithValueField(18, 4, name: "PRESC");
            }
            else
            {
                commonConfigurationRegister
                    .WithReservedBits(18, 4);
            }

            if(hasVbatPin)
            {
                commonConfigurationRegister
                    .WithTaggedFlag("VBATEN", 24);
            }
            else
            {
                commonConfigurationRegister
                    .WithReservedBits(24, 1);
            }

            // DEEPPWD and the two bit ADVREGEN both reset to 1 at bit 29
            var controlRegister = new DoubleWordRegister(this, hasDeepPowerDown || voltageRegulator == VoltageRegulator.TwoBit ? 0x20000000u : 0x0u)
                    .WithFlag(0, valueProviderCallback: _ => enabled, writeCallback: (_, val) =>
                        {
                            if(val)
                            {
                                enabled = true;
                                adcReadyFlag.Value = true;
                                UpdateInterrupts();
                            }
                        }, name: "ADEN")
                    // Reading one from below field would mean that command is in progress. This is never the case in this model
                    .WithFlag(1, valueProviderCallback: _ => false, writeCallback: (_, val) => { if(val) enabled = false; }, name: "ADDIS")
                    // Reading one from this field means that conversion is in progress
                    .WithFlag(2, out startFlag, writeCallback: (_, val) =>
                        {
                            if(val)
                            {
                                if(externalTrigger || continuous.Value)
                                {
                                    samplingThread.Start();
                                }
                                else
                                {
                                    StartSampling();
                                }
                            }
                        },name: "ADSTART")
                    // Reading one from below field would mean that command is in progress. This is never the case in this model
                    .WithFlag(4, valueProviderCallback: _ => false,  writeCallback: (_, val) =>
                        {
                            if(val)
                            {
                                samplingThread.Stop();
                                sequenceInProgress = false;
                            }
                        }, name: "ADSTP")
                    .WithReservedBits(30, 1)
                    // Calibration completes immediately
                    .WithFlag(31, valueProviderCallback: _ => false, writeCallback: (_, val) =>
                        {
                            if(val && hasEndOfCalibration)
                            {
                                endOfCalibrationFlag.Value = true;
                                UpdateInterrupts();
                            }
                        }, name: "ADCAL");

            switch(voltageRegulator)
            {
            case VoltageRegulator.OneBit:
                controlRegister
                    .WithFlag(28, out adcRegulatorEnable, name: "ADVREGEN");
                break;
            case VoltageRegulator.TwoBit:
                // 0b10: disabled, 0b00: intermediate, 0b01: enabled
                controlRegister
                    .WithValueField(28, 2, out adcRegulatorState, name: "ADVREGEN");
                break;
            default:
                controlRegister
                    .WithReservedBits(28, 1);
                break;
            }

            if(hasDeepPowerDown)
            {
                controlRegister
                    .WithFlag(29, name: "DEEPPWD"); // no actual logic, but software expects to read the value back
            }
            else if(voltageRegulator != VoltageRegulator.TwoBit)
            {
                controlRegister
                    .WithReservedBits(29, 1);
            }

            if(hasLinearityCalibration)
            {
                controlRegister
                    .WithReservedBits(6, 2)
                    .WithFlags(8, 2, name: "BOOST")
                    .WithReservedBits(10, 6)
                    .WithFlag(16, name: "ADCALLIN")
                    .WithFlag(22, valueProviderCallback: _ => true, name: "LINCALRDYW1")
                    .WithFlag(23, valueProviderCallback: _ => true, name: "LINCALRDYW2")
                    .WithFlag(24, valueProviderCallback: _ => true, name: "LINCALRDYW3")
                    .WithFlag(25, valueProviderCallback: _ => true, name: "LINCALRDYW4")
                    .WithFlag(26, valueProviderCallback: _ => true, name: "LINCALRDYW5")
                    .WithFlag(27, valueProviderCallback: _ => true, name: "LINCALRDYW6");
            }
            else
            {
                controlRegister
                    .WithReservedBits(6, 10)
                    .WithReservedBits(16, 11);
            }

            if(hasChannelInjection)
            {
                controlRegister
                    .WithFlag(3, out startInjectionFlag, changeCallback: (_, val) =>
                        {
                            if(val)
                            {
                                StartInjectedSampling();
                            }
                        }, name: "JADSTART")
                    .WithFlag(5, valueProviderCallback: _ => false, writeCallback: (_, val) =>
                        {
                            if(val)
                            {
                                startInjectionFlag.Value = false;
                            }
                        }, name: "JADSTP");
            }
            else
            {
                controlRegister
                    .WithReservedBits(3, 1)
                    .WithReservedBits(5, 1);
            }

            var dataRegister = new DoubleWordRegister(this)
                .WithValueField(0, 16, out data, FieldMode.Read, readCallback: (_, __) =>
                    {
                        endOfConversionFlag.Value = false;
                        // This function call must be delayed to avoid deadlock on registers access
                        if(sequenceInProgress)
                        {
                            machine.LocalTimeSource.ExecuteInNearestSyncedState((___) => SampleNextChannel());
                        }
                        UpdateInterrupts();
                    }, name: "DATA")
                .WithReservedBits(16, 16);

            var registers = new Dictionary<long, DoubleWordRegister>
            {
                {(long)Registers.InterruptAndStatus, isrRegister},
                {(long)Registers.InterruptEnable, interruptEnableRegister},
                {(long)Registers.Control, controlRegister},
                {(long)Registers.Configuration1, configurationRegister1},
                {(long)Registers.Configuration2, configurationRegister2},
                {(long)Registers.DataRegister, dataRegister},
                {(long)Registers.CommonConfiguration, commonConfigurationRegister},
            };

            if(hasChannelSequence)
            {
                BuildRegularSequenceRegisters(registers, MaximumSequenceLength);
            }

            BuildSampingTimeRegisters(registers, samplingTime);

            // Optional registers
            if(hasChannelSelect)
            {
                registers.Add(GetChannelSelectionRegister(), new DoubleWordRegister(this)
                    .WithFlags(0, ADCChannelCount,
                           valueProviderCallback: (id, __) => channelSelected[id],
                           writeCallback: (id, _, val) => { this.Log(LogLevel.Debug, "Channel {0} enable set as {1}", id, val); channelSelected[id] = val; })
                    .WithReservedBits(ADCChannelCount, 32 - ADCChannelCount)
                );
            }

            BuildWatchdogRegisters(registers);

            if(hasCalibration)
            {
                var calibrationFactor = new DoubleWordRegister(this);
                switch(adcVersion)
                {
                case AdcVersion.V2:
                    calibrationFactor
                        .WithValueField(0, 7, name: "CALFACT_S")
                        .WithReservedBits(7, 9)
                        .WithValueField(16, 7, name: "CALFACT_D")
                        .WithReservedBits(23, 9);
                    break;
                case AdcVersion.V3:
                    calibrationFactor
                        .WithValueField(0, 11, name: "CALFACT_S")
                        .WithReservedBits(11, 5)
                        .WithValueField(16, 11, name: "CALFACT_D")
                        .WithReservedBits(27, 5);
                    break;
                default:
                    calibrationFactor
                        .WithValueField(0, 7, name: "CALFACT")
                        .WithReservedBits(7, 25);
                    break;
                }
                registers.Add(GetCalibrationFactorRegister(), calibrationFactor);
            }

            if(hasLinearityCalibration)
            {
                // Also present on U5 family, which does not have linearity calibration
                registers.Add((long)RegistersV3.CalibrationFactor2, new DoubleWordRegister(this)
                    .WithValueField(0, 7, name: "CALFACT2")
                    .WithReservedBits(7, 25));
            }

            if(hasPowerRegister)
            {
                registers.Add((long)RegistersV4.Power, new DoubleWordRegister(this)
                    .WithTaggedFlag("AUTOFF", 0)
                    .WithTaggedFlag("DPD", 1) // Deep-power-down mode
                    .WithReservedBits(2, 30));
            }

            if(hasChannelInjection)
            {
                registers.Add((long)Registers.InjectedSequence, new DoubleWordRegister(this)
                        .WithValueField(0, 2, out injectedSequenceLength, name: "JL")
                        .WithTag("JEXTSEL", 2, 4)
                        .WithTag("JEXTEN", 7, 2)
                        .WithValueField(9, 4, out injectedSequence[0], name: "JSQ1")
                        .WithReservedBits(14, 1)
                        .WithValueField(15, 4, out injectedSequence[1], name: "JSQ2")
                        .WithReservedBits(20, 1)
                        .WithValueField(21, 4, out injectedSequence[2], name: "JSQ3")
                        .WithReservedBits(26, 1)
                        .WithValueField(27, 4, out injectedSequence[3], name: "JSQ4"));

                registers.Add((long)Registers.InjectedChannel1, new DoubleWordRegister(this)
                        .WithValueField(0, 32, out injectedData[0], name: "JDATA1"));
                registers.Add((long)Registers.InjectedChannel2, new DoubleWordRegister(this)
                        .WithValueField(0, 32, out injectedData[1], name: "JDATA2"));
                registers.Add((long)Registers.InjectedChannel3, new DoubleWordRegister(this)
                        .WithValueField(0, 32, out injectedData[2], name: "JDATA3"));
                registers.Add((long)Registers.InjectedChannel4, new DoubleWordRegister(this)
                        .WithValueField(0, 32, out injectedData[3], name: "JDATA4"));
            }

            if(hasOffset)
            {
                for(uint i = 0; i < 4; i++)
                {
                    registers.Add((long)Registers.OffsetRegister1 + 4 * i, new DoubleWordRegister(this)
                        .WithTag("OFFSET", 0, 12)
                        .WithReservedBits(12, 14)
                        .WithTag("OFFSET_CH", 26, 5)
                        .WithTaggedFlag("OFFSET_EN", 31)
                    );
                }
            }

            if(hasDifferentialMode)
            {
                registers.Add(GetDifferentialModeRegister(), new DoubleWordRegister(this)
                    .WithTag("DIFSEL", 0, 19)
                    .WithReservedBits(19, 13)
                );
            }

            if(dualMode)
            {
                /* dualMode is not really supported, let's mock ADEN and ADDIS so software can
                 * disable the ADC2 and checks that it is disabled.
                 */
                registers.Add((long)Registers.Control + 0x100, new DoubleWordRegister(this)
                    .WithTaggedFlag("ADEN", 0)
                    .WithFlag(1, valueProviderCallback: _ => false, name: "ADDIS")
                );
            }

            if(hasChannelPreselection)
            {
                registers.Add((long)Registers.ChannelPreselection, new DoubleWordRegister(this)
                    .WithFlags(0, ADCChannelCount, out preselectedChannels, name: "PCSELn")
                );
            }

            return registers;
        }

        private bool IsRegulatorEnabled() => voltageRegulator switch
        {
            VoltageRegulator.OneBit => adcRegulatorEnable.Value,
            VoltageRegulator.TwoBit => adcRegulatorState.Value == 0b01,
            _ => true
        };

        private long GetChannelSelectionRegister() => adcVersion switch
        {
            AdcVersion.V1 => (long)RegistersV1.ChannelSelection,
            AdcVersion.V4 => (long)RegistersV4.ChannelSelection,
            _ => throw new ConstructionException($"ADC_CHSELR does not exist in {adcVersion}")
        };

        private long GetCalibrationFactorRegister() => adcVersion switch
        {
            AdcVersion.V1 => (long)RegistersV1.CalibrationFactor,
            AdcVersion.V2 => (long)RegistersV2.CalibrationFactor,
            AdcVersion.V3 => (long)RegistersV3.CalibrationFactor,
            AdcVersion.V4 => (long)RegistersV4.CalibrationFactor,
            _ => throw new ConstructionException($"ADC_CALFACT does not exist in {adcVersion}")
        };

        private long GetDifferentialModeRegister() => adcVersion switch
        {
            AdcVersion.V2 => (long)RegistersV2.DifferentialMode,
            AdcVersion.V3 => (long)RegistersV3.DifferentialMode,
            _ => throw new ConstructionException($"ADC_DIFSEL does not exist in {adcVersion}")
        };

        private void BuildWatchdogRegisters(Dictionary<long, DoubleWordRegister> registers)
        {
            RegistersV3 GetLowerThresholdRegister(int i) => i switch
            {
                0 => RegistersV3.Watchdog1LowerThreshold,
                1 => RegistersV3.Watchdog2LowerThreshold,
                2 => RegistersV3.Watchdog3LowerThreshold,
                _ => throw new ConstructionException($"ADC_LTR{i + 1} does not exist")
            };

            RegistersV3 GetHigherThresholdRegister(int i) => i switch
            {
                0 => RegistersV3.Watchdog1HigherThreshold,
                1 => RegistersV3.Watchdog2HigherThreshold,
                2 => RegistersV3.Watchdog3HigherThreshold,
                _ => throw new ConstructionException($"ADC_HTR{i + 1} does not exist")
            };

            long GetThresholdRegister(int i) => (adcVersion, i) switch
            {
                (AdcVersion.V1, 0) => (long)RegistersV1.Watchdog1Threshold,
                (AdcVersion.V1, 1) => (long)RegistersV1.Watchdog2Threshold,
                (AdcVersion.V1, 2) => (long)RegistersV1.Watchdog3Threshold,
                (AdcVersion.V2, 0) => (long)RegistersV2.Watchdog1Threshold,
                (AdcVersion.V2, 1) => (long)RegistersV2.Watchdog2Threshold,
                (AdcVersion.V2, 2) => (long)RegistersV2.Watchdog3Threshold,
                (AdcVersion.V4, 0) => (long)RegistersV4.Watchdog1Threshold,
                (AdcVersion.V4, 1) => (long)RegistersV4.Watchdog2Threshold,
                (AdcVersion.V4, 2) => (long)RegistersV4.Watchdog3Threshold,
                _ => throw new ConstructionException($"ADC_TR{i + 1} does not exist")
            };

            Registers GetConfigurationRegister(int i) => i switch
            {
                1 => Registers.Watchdog2Configuration,
                2 => Registers.Watchdog3Configuration,
                _ => throw new ConstructionException($"ADC_AWD{i + 1}CH does not exist")
            };

            analogWatchdogHighValues = new IValueRegisterField[WatchdogCount];
            analogWatchdogLowValues = new IValueRegisterField[WatchdogCount];
            analogWatchdogSelectedChannels = new Dictionary<int, IFlagRegisterField[]>();

            for(var i = 0; i < WatchdogCount; i++)
            {
                if(hasSeparateThresholdRegisters)
                {
                    registers.Add((long)GetLowerThresholdRegister(i), new DoubleWordRegister(this)
                        .WithValueField(0, 26, out analogWatchdogLowValues[i], name: $"LT{i + 1}")
                        .WithReservedBits(26, 6));

                    registers.Add((long)GetHigherThresholdRegister(i), new DoubleWordRegister(this)
                        .WithValueField(0, 26, out analogWatchdogHighValues[i], name: $"HT{i + 1}")
                        .WithReservedBits(26, 6));
                }
                else
                {
                    registers.Add(GetThresholdRegister(i), new DoubleWordRegister(this)
                        .WithValueField(0, 12, out analogWatchdogLowValues[i], name: $"LT{i + 1}")
                        .WithReservedBits(12, 4)
                        .WithValueField(16, 12, out analogWatchdogHighValues[i], name: $"HT{i + 1}")
                        .WithReservedBits(28, 4));
                }
                if(i > 0)
                {
                    registers.Add((long)GetConfigurationRegister(i), new DoubleWordRegister(this)
                        .WithFlags(0, ADCChannelCount, out var selectedChannels, name: $"AWD{i + 1}CH")
                        .WithReservedBits(ADCChannelCount, 31 - ADCChannelCount));
                    analogWatchdogSelectedChannels.Add(i, selectedChannels);
                }
            }
        }

        private void BuildRegularSequenceRegisters(Dictionary<long, DoubleWordRegister> registers, int sequenceCount)
        {
            DoubleWordRegister BuildRegularSequenceRegister(int offset, int sequenceCount, bool containsSequenceLength)
            {
                var register = new DoubleWordRegister(this);
                var sequenceOffset = 0;

                if(containsSequenceLength)
                {
                    register.WithValueField(0, 4, out regularSequenceLength, name: "L")
                        .WithReservedBits(4, 2);
                    sequenceOffset = 6;
                }

                for(var i = 0; i < sequenceCount; i++)
                {
                    var sequenceFieldWidth = 5;
                    var sequenceIndex = offset + i;

                    register
                        .WithValueField(sequenceOffset, sequenceFieldWidth, out regularSequence[sequenceIndex], name: $"SQ{sequenceIndex + 1}")
                        .WithReservedBits(sequenceOffset + sequenceFieldWidth, 1);
                    sequenceOffset += sequenceFieldWidth + 1;
                }
                register.WithReservedBits(sequenceOffset, register.RegisterWidth - sequenceOffset);
                return register;
            }

            Registers GetSequenceRegister(int i) => i switch
            {
                0 => Registers.RegularSequence1,
                1 => Registers.RegularSequence2,
                2 => Registers.RegularSequence3,
                3 => Registers.RegularSequence4,
                _ => throw new ConstructionException($"ADC_SQR{i} does not exist")
            };

            var sequenceOffset = 0;
            for(var i = 0; i < 4; i++)
            {
                var sequencesPerRegister = i == 0 ? 4 : 5;
                var sequencesInRegister = Math.Min(sequencesPerRegister, sequenceCount - sequenceOffset);

                var register = BuildRegularSequenceRegister(sequenceOffset, sequencesInRegister, i == 0);

                registers.Add((long)GetSequenceRegister(i), register);
                sequenceOffset += sequencesPerRegister;
            }
        }

        private void BuildSampingTimeRegisters(Dictionary<long, DoubleWordRegister> registers, SamplingTime samplingTime)
        {
            if(samplingTime == SamplingTime.OneForAll)
            {
                registers.Add((long)Registers.SamplingTime, new DoubleWordRegister(this)
                    .WithTag("SMP", 0, 3)
                    .WithReservedBits(3, 29)
                );
            }
            else if(samplingTime == SamplingTime.TwoSelections)
            {
                /* SMP1 and SMP2 defined in 0-2 and 4-6, other bits from 8 to 8 + channelCount are
                 * to select SMP1 or SMP2.
                 */
                var smpr = new DoubleWordRegister(this)
                    .WithTag("SMP1", 0, 3)
                    .WithReservedBits(3, 1)
                    .WithTag("SMP2", 4, 3)
                    .WithReservedBits(7, 1);
                for(int i = 0; i < ADCChannelCount; i++)
                {
                    smpr.Tag($"SMPSEL{i}", 8 + i, 1);
                }
                smpr.Reserved(32 - (24 - ADCChannelCount), 24 - ADCChannelCount);
                registers.Add((long)Registers.SamplingTime, smpr);
            }
            else if(samplingTime == SamplingTime.PerChannel)
            {
                /* 3 bits per channel, spread over 2 registers if needed. */
                var smpr1 = new DoubleWordRegister(this);
                var smpr2 = new DoubleWordRegister(this);

                for(int i = 0; i < ADCChannelCount && i < 10; i++)
                {
                    smpr1.Tag($"SMP{i}", 3 * i, 3);
                }
                var reservedBitsEntries = ADCChannelCount > 10 ? 0 : (10 - ADCChannelCount);
                var reservedBitsWidth = 2 + reservedBitsEntries * 3;
                smpr1.Reserved(32 - reservedBitsWidth, reservedBitsWidth);
                registers.Add((long)Registers.SamplingTime, smpr1);

                for(int i = 10; i < ADCChannelCount; i++)
                {
                    smpr2.Tag($"SMP{i}", 3 * (i - 10), 3);
                }
                reservedBitsEntries = ADCChannelCount > 20 ? 0 : (20 - ADCChannelCount);
                reservedBitsWidth = 2 + reservedBitsEntries * 3;
                smpr2.Reserved(32 - reservedBitsWidth, reservedBitsWidth);
                registers.Add((long)Registers.SamplingTime2, smpr2);
            }
        }

        private ushort ResolutionToBits(ulong resolution)
        {
            if(resolutionRange == ResolutionRange.Bits8_16)
            {
                // STM32H74x/75x revision V encoding, 0b100 is the revision Y 8 bit resolution
                switch(resolution)
                {
                case 0b000: return 16;
                case 0b001: return 14;
                case 0b010: return 12;
                case 0b011: return 10;
                case 0b100: return 8;
                case 0b101: return 14;
                case 0b110: return 12;
                case 0b111: return 8;
                }
            }
            else if(resolutionRange == ResolutionRange.Bits6_12)
            {
                switch(resolution)
                {
                case 0b00: return 12;
                case 0b01: return 10;
                case 0b10: return 8;
                case 0b11: return 6;
                }
            }
            throw new NotImplementedException($"Missing {resolutionRange} bit support");
        }

        private ushort MinResolutionBits => resolutionRange == ResolutionRange.Bits8_16 ? (ushort)8 : (ushort)6;

        private ushort MaxResolutionBits => resolutionRange == ResolutionRange.Bits8_16 ? (ushort)16 : (ushort)12;

        private IEnumRegisterField<Align> align;
        // While watchdogs 2 and 3 use bitfields for selecting channels to watch
        private IDictionary<int, IFlagRegisterField[]> analogWatchdogSelectedChannels;
        // Watchdog 1 either watches all channels or a single channel
        private IValueRegisterField analogWatchdogChannel;

        private IValueRegisterField data;
        private IFlagRegisterField analogWatchdogSingleChannel;
        private IFlagRegisterField endOfSequenceInterruptEnable;
        private IFlagRegisterField endOfCalibrationInterruptEnable;
        private IFlagRegisterField endOfSamplingInterruptEnable;
        private IFlagRegisterField endOfConversionInterruptEnable;
        private IFlagRegisterField[] analogWatchdogsInterruptEnable;
        private IFlagRegisterField adcReadyInterruptEnable;
        private IFlagRegisterField adcOverrunInterruptEnable;
        private IFlagRegisterField endOfSequenceFlag;
        private IFlagRegisterField endOfCalibrationFlag;
        private IFlagRegisterField endOfConversionFlag;
        private IFlagRegisterField[] analogWatchdogFlags;
        private IFlagRegisterField adcReadyFlag;
        private IFlagRegisterField adcRegulatorEnable;
        private IValueRegisterField adcRegulatorState;

        private IFlagRegisterField adcOverrunFlag;
        private IFlagRegisterField overrunMode;
        private IFlagRegisterField continuous;
        private IFlagRegisterField waitFlag;
        private IFlagRegisterField startFlag;
        private IFlagRegisterField analogWatchdogEnable;

        private IFlagRegisterField endOfConversionInjectedFlag;
        private IFlagRegisterField endOfSequenceInjectedFlag;
        private IFlagRegisterField endOfSequenceInjectedInterruptEnable;
        private IFlagRegisterField endOfConversionInjectedInterruptEnable;

        private IFlagRegisterField dmaEnabled;
        private ScanDirection scanDirection;
        private IValueRegisterField resolution;
        private IFlagRegisterField endOfSamplingFlag;

        private IValueRegisterField regularSequenceLength;

        private int currentChannel;
        private int sequenceCounter;
        private bool enabled;
        private bool externalTrigger;
        private bool sequenceInProgress;
        private bool awaitingConversion;
        private IFlagRegisterField startInjectionFlag;
        private IValueRegisterField injectedSequenceLength;
        private int injectedSequenceCounter;
        private IFlagRegisterField[] preselectedChannels;
        private IValueRegisterField[] analogWatchdogHighValues;
        private IValueRegisterField[] analogWatchdogLowValues;
        private readonly bool[] channelSelected;
        private readonly IValueRegisterField[] regularSequence = new IValueRegisterField[MaximumSequenceLength];
        private readonly IValueRegisterField[] injectedSequence = new IValueRegisterField[MaximumInjectedSequenceLength];
        private readonly IValueRegisterField[] injectedData = new IValueRegisterField[MaximumInjectedSequenceLength];

        private readonly IDMA dma;
        private readonly int dmaChannel;
        private readonly AdcVersion adcVersion;
        private readonly VoltageRegulator voltageRegulator;
        private readonly bool hasChannelSelect;
        private readonly bool hasEndOfCalibration;
        private readonly bool hasSeparateThresholdRegisters;
        private readonly ResolutionRange resolutionRange;
        private readonly bool hasChannelPreselection;
        private readonly uint externalEventFrequency;
        private readonly double referenceVoltage;
        private readonly IManagedThread samplingThread;
        private readonly DoubleWordRegisterCollection registers;
        private readonly IMachine machine;

        private readonly int WatchdogCount;
        private readonly bool hasChannelInjection;
        private const int MaximumSequenceLength = 16;
        private const int MaximumInjectedSequenceLength = 4;

        public enum SamplingTime
        {
            OneForAll,
            TwoSelections,
            PerChannel,
        }

        public enum ResolutionRange
        {
            Bits8_16,
            Bits6_12,
        }

        public enum VoltageRegulator
        {
            None,   // F0, N6, MP2
            OneBit, // L0, G0, C0, U0, WL, WBA, L4, L5, G4, H5, H7, U5, U3, C5
            TwoBit, // F3: 0b10 disabled, 0b00 intermediate, 0b01 enabled
        }

        // Numbering is derived from CMSIS ADC_TypeDef address-compatible layouts and is not ST's ADC_VER_Vx
        public enum AdcVersion
        {
            V1, // F0, L0, C0, G0, U0, WL: TR/AWD1TR@0x20, AWD2TR@0x24, CHSELR@0x28, AWD3TR@0x2C, CALFACT@0xB4
            V2, // F3, L4, L5, WB, G4, H5, H7RS, MP13: TR1-3@0x20-0x28, SQR1-4, DIFSEL@0xB0, CALFACT@0xB4
            V3, // H7, MP1: PCSEL, LTR1/HTR1@0x20, LTR2-3/HTR2-3@0xB0-0xBC, DIFSEL@0xC0, CALFACT@0xC4, CALFACT2@0xC8
            V4, // WBA: V1 layout + PWRR@0x44, CALFACT@0xC4
            V5, // U5, U3, N6, MP2, C5: AWD1-3 LTR/HTR@0xA8-0xBC, GCOMP@0x70, CALFACT@0xC4, OR@0xD0
        }

        private enum ScanDirection
        {
            Ascending  = 0b0,
            Descending = 0b1,
        }

        private enum Align
        {
            Right = 0x0,
            Left  = 0x1,
        }

        private enum Registers
        {
            InterruptAndStatus     = 0x00, // ADC_ISR
            InterruptEnable        = 0x04, // ADC_IER
            Control                = 0x08, // ADC_CR
            Configuration1         = 0x0C, // ADC_CFGR1
            Configuration2         = 0x10, // ADC_CFGR2
            SamplingTime           = 0x14, // ADC_SMPR/ADC_SMPR1
            SamplingTime2          = 0x18, // ADC_SMPR2
            ChannelPreselection    = 0x1C, // ADC_PCSEL
            // Gap intended
            RegularSequence1       = 0x30, // ADC_SQR1
            RegularSequence2       = 0x34, // ADC_SQR2
            RegularSequence3       = 0x38, // ADC_SQR3
            RegularSequence4       = 0x3C, // ADC_SQR4
            DataRegister           = 0x40, // ADC_DR
            // Gap intended
            InjectedSequence       = 0x4C, // ADC_JSQR
            // Gap intended
            OffsetRegister1        = 0x60, // ADC_OFR1
            OffsetRegister2        = 0x64, // ADC_OFR2
            OffsetRegister3        = 0x68, // ADC_OFR3
            OffsetRegister4        = 0x6C, // ADC_OFR4
            // Gap intended
            InjectedChannel1       = 0x80, // ADC_JDR1
            InjectedChannel2       = 0x84, // ADC_JDR2
            InjectedChannel3       = 0x88, // ADC_JDR3
            InjectedChannel4       = 0x8C, // ADC_JDR4
            // Gap intended
            Watchdog2Configuration = 0xA0, // ADC_AWD2CR
            Watchdog3Configuration = 0xA4, // ADC_AWD3CR
            // Gap intended
            CommonConfiguration    = 0x308, // ADC_CCR
        }

        private enum RegistersV1
        {
            Watchdog1Threshold     = 0x20, // ADC_TR/ADC_AWD1TR
            Watchdog2Threshold     = 0x24, // ADC_AWD2TR
            ChannelSelection       = 0x28, // ADC_CHSELR
            Watchdog3Threshold     = 0x2C, // ADC_AWD3TR
            // Gap intended
            CalibrationFactor      = 0xB4, // ADC_CALFACT
        }

        private enum RegistersV2
        {
            Watchdog1Threshold     = 0x20, // ADC_TR1
            Watchdog2Threshold     = 0x24, // ADC_TR2
            Watchdog3Threshold     = 0x28, // ADC_TR3
            // Gap intended
            DifferentialMode       = 0xB0, // ADC_DIFSEL
            CalibrationFactor      = 0xB4, // ADC_CALFACT
        }

        private enum RegistersV3
        {
            Watchdog1LowerThreshold  = 0x20, // ADC_LTR1
            Watchdog1HigherThreshold = 0x24, // ADC_HTR1
            // Gap intended
            Watchdog2LowerThreshold  = 0xB0, // ADC_LTR2
            Watchdog2HigherThreshold = 0xB4, // ADC_HTR2
            Watchdog3LowerThreshold  = 0xB8, // ADC_LTR3
            Watchdog3HigherThreshold = 0xBC, // ADC_HTR3
            DifferentialMode         = 0xC0, // ADC_DIFSEL
            CalibrationFactor        = 0xC4, // ADC_CALFACT
            CalibrationFactor2       = 0xC8, // ADC_CALFACT2
        }

        private enum RegistersV4
        {
            Watchdog1Threshold     = 0x20, // ADC_AWD1TR
            Watchdog2Threshold     = 0x24, // ADC_AWD2TR
            ChannelSelection       = 0x28, // ADC_CHSELR
            Watchdog3Threshold     = 0x2C, // ADC_AWD3TR
            // Gap intended
            Power                  = 0x44, // ADC_PWRR
            // Gap intended
            CalibrationFactor      = 0xC4, // ADC_CALFACT
        }
    }
}
