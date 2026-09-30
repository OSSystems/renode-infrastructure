//
// Copyright (c) 2010-2026 Antmicro
// Copyright (c) 2026 OS Systems
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.DMA;

namespace Antmicro.Renode.Peripherals.Analog
{
    public class STM32F0_ADC : STM32_ADC_Common
    {
        public STM32F0_ADC(IMachine machine, double referenceVoltage, uint externalEventFrequency, int dmaChannel = 0, IDMA dmaPeripheral = null)
            : base(
                machine,
                referenceVoltage,
                externalEventFrequency,
                dmaChannel,
                dmaPeripheral,
                // Base class configuration
                adcVersion: AdcVersion.V1,
                watchdogCount: 1,
                hasCalibration: false,
                voltageRegulator: VoltageRegulator.None,
                hasDeepPowerDown: false,
                hasLowFrequencyMode: false,
                hasOversampler: false,
                hasLowFrequencyTrigger: false,
                channelCount: 19,
                hasPrescaler: false,
                hasVbatPin: true,
                hasChannelSequence: false,
                hasOffset: false,
                hasDifferentialMode: false,
                samplingTime: SamplingTime.OneForAll,
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
