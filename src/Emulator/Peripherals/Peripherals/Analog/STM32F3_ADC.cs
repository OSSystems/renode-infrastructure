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
    public class STM32F3_ADC : STM32_ADC_Common
    {
        public STM32F3_ADC(IMachine machine, double referenceVoltage, uint externalEventFrequency,
                           int dmaChannel = 0, IDMA dmaPeripheral = null, bool dualMode = false)
            : base(
                machine,
                referenceVoltage,
                externalEventFrequency,
                dmaChannel,
                dmaPeripheral,
                // Base class configuration
                adcVersion: AdcVersion.V2,
                watchdogCount: 3,
                hasCalibration: true,
                voltageRegulator: VoltageRegulator.TwoBit,
                hasDeepPowerDown: false,
                channelCount: 19,
                hasPrescaler: true,
                hasVbatPin: false,
                hasChannelSequence: true,
                hasOffset: true,
                hasDifferentialMode: true,
                samplingTime: SamplingTime.PerChannel,
                dualMode: dualMode,
                hasLinearityCalibration: false,
                hasChannelInjection: false,
                resolutionRange: ResolutionRange.Bits6_12,
                hasChannelPreselection: false,
                hasScanDirection: false
            )
        { }
    }
}
