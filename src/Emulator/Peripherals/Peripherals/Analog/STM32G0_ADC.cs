//
// Copyright (c) 2010-2024 Antmicro
// Copyright (c) 2023-2025 OS Systems
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.DMA;

namespace Antmicro.Renode.Peripherals.Analog
{
    public class STM32G0_ADC : STM32_ADC_Common
    {
        public STM32G0_ADC(IMachine machine, double referenceVoltage, uint externalEventFrequency, int dmaChannel = 0, IDMA dmaPeripheral = null)
            : base(
                machine,
                referenceVoltage,
                externalEventFrequency,
                dmaChannel,
                dmaPeripheral,
                // Base class configuration
                adcVersion: AdcVersion.V1,
                watchdogCount: 3,
                hasCalibration: true,
                voltageRegulator: VoltageRegulator.OneBit,
                hasDeepPowerDown: false,
                hasLowFrequencyMode: true,
                hasOversampler: true,
                hasLowFrequencyTrigger: true,
                channelCount: 19,
                hasPrescaler: true,
                hasVbatPin: true,
                hasChannelSequence: true,
                hasOffset: false,
                hasDifferentialMode: false,
                samplingTime: SamplingTime.TwoSelections,
                dualMode: false,
                hasLinearityCalibration: false,
                hasChannelInjection: false,
                resolutionRange: ResolutionRange.Bits6_12,
                hasChannelPreselection: false,
                hasScanDirection: true
            )
        { }
    }
}
