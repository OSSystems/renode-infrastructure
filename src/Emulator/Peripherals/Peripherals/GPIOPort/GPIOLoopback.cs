//
// Copyright (c) 2026 Freedom Veiculos Eletricos
// SPDX-License-Identifier: UNLICENSED
//
// GPIO Loopback peripheral for Renode
//
// Bridges a GPIO output from one port/pin to an input on
// another (or the same) port. Works around the
// STM32_GPIOPort.WriteState limitation where same-port
// loopback via "portA.5 -> portA@6" is clobbered because
// WriteState iterates all 16 pins with a pre-computed
// bitmap, overwriting externally-driven input state.
//
// Usage in .repl:
//   loopback: Miscellaneous.GPIOLoopback @ gpioa 5
//       target: gpioa
//       pin: 6
//
// This receives on gpioa.Connections[5], then drives
// gpioa.OnGPIO(6, value) via a deferred action so the
// state update happens after WriteState completes.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // Same-port GPIO loopback for Renode.
    //
    // Bridges an output pin to an input pin, working
    // around the STM32_GPIOPort.WriteState limitation
    // where same-port loopback is clobbered.
    //
    // Uses HandleTimeDomainEvent to defer target.OnGPIO
    // until after WriteState completes. This ensures:
    //  - State[pin] is set correctly for IDR reads
    //  - Connections[pin] fires EXTI for interrupts
    //  - Only one ISR per edge (no double trigger)
    //
    // No IRQ output needed — EXTI is driven via the
    // deferred target.OnGPIO → Connections path.
    //
    // Usage in .repl / .resc:
    //   loopback: Miscellaneous.GPIOLoopback @ srcPort srcPin { target: dstPort; pin: N }
    //   srcPort: { srcPin -> loopback@0 }
    //
    public class GPIOLoopback : IGPIOReceiver
    {
        public GPIOLoopback(
            IMachine machine,
            BaseGPIOPort target,
            int pin)
        {
            this.machine = machine;
            this.target = target;
            this.pin = pin;
        }

        public void OnGPIO(int number, bool value)
        {
            // Defer to after WriteState completes.
            // Guard: skip during platform loading when
            // no virtual time domain is registered.
            TimeStamp vts;
            if(TimeDomainsManager.Instance
                .TryGetVirtualTimeStamp(out vts))
            {
                machine.HandleTimeDomainEvent<bool>(
                    v => target.OnGPIO(pin, v),
                    value,
                    vts);
            }
        }

        public void Reset()
        {
        }

        private readonly IMachine machine;
        private readonly BaseGPIOPort target;
        private readonly int pin;
    }
}
