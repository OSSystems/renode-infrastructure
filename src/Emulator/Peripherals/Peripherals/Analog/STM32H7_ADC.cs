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
    public class STM32H7_ADC : STM32_ADC_Common
    {
        public STM32H7_ADC(IMachine machine, double referenceVoltage, uint externalEventFrequency, int dmaChannel = 0, IDMA dmaPeripheral = null)
            : base(
                machine,
                referenceVoltage,
                externalEventFrequency,
                dmaChannel,
                dmaPeripheral,
                // Base class configuration
                adcVersion: AdcVersion.V3,
                watchdogCount: 3,
                hasCalibration: true,
                voltageRegulator: VoltageRegulator.OneBit,
                hasDeepPowerDown: true,
                hasLowFrequencyMode: false,
                channelCount: 19,
                hasPrescaler: true,
                hasVbatPin: true,
                hasChannelSequence: true,
                hasOffset: true,
                hasDifferentialMode: true,
                samplingTime: SamplingTime.PerChannel,
                dualMode: true,
                hasLinearityCalibration: true,
                hasChannelInjection: true,
                resolutionRange: ResolutionRange.Bits8_16,
                hasChannelPreselection: true,
                hasScanDirection: false
            )
        { }
    }
}
