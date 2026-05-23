//
// Copyright (c) 2026 Freedom Veiculos Eletricos
// SPDX-License-Identifier: UNLICENSED
//
// Single-wire UART hub for Renode simulation.
//
// Behaves like UARTHub but always echoes to the sender
// (loopback) and adds a fixed 100us delay per character.
// This models single-wire half-duplex where the
// transmitter sees its own echo after a propagation
// delay, giving the FSM time to transition out of
// TX_IN_PROGRESS before receiving the echo byte.
//
using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Migrant.Hooks;
using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.UART
{
    public static class SingleWireHubExtensions
    {
        public static void CreateSingleWireHub(
            this Emulation emulation, string name)
        {
            emulation.ExternalsManager.AddExternal(
                new SingleWireHub(), name);
        }
    }

    public sealed class SingleWireHub
        : IExternal, IHasOwnLife, IConnectable<IUART>
    {
        public SingleWireHub()
        {
            uarts = new Dictionary<IUART, Action<byte>>();
            locker = new object();
        }

        public void AttachTo(IUART uart)
        {
            lock(locker)
            {
                if(uarts.ContainsKey(uart))
                {
                    throw new RecoverableException(
                        "Cannot attach to the provided UART"
                        + " as it is already registered in"
                        + " this hub.");
                }

                if(uarts.Count >= MaxConnections)
                {
                    throw new RecoverableException(
                        "SingleWireHub supports at most "
                        + MaxConnections + " connections.");
                }

                Action<byte> handler = value =>
                {
                    HandleCharReceived(value, uart);
                };
                uarts.Add(uart, handler);
                uart.CharReceived += handler;
            }
        }

        public void DetachFrom(IUART uart)
        {
            lock(locker)
            {
                if(!uarts.ContainsKey(uart))
                {
                    throw new RecoverableException(
                        "Cannot detach from the provided"
                        + " UART as it is not registered"
                        + " in this hub.");
                }

                uart.CharReceived -= uarts[uart];
                uarts.Remove(uart);
            }
        }

        public void Start()
        {
            Resume();
        }

        public void Pause()
        {
            started = false;
        }

        public void Resume()
        {
            started = true;
        }

        public bool IsPaused => !started;

        private void HandleCharReceived(
            byte value, IUART sender)
        {
            if(!started)
            {
                return;
            }

            var now =
                TimeDomainsManager.Instance.VirtualTimeStamp;
            var when = new TimeStamp(
                now.TimeElapsed + CharDelay, now.Domain);

            lock(locker)
            {
                foreach(var uart in uarts.Keys)
                {
                    uart.GetMachine()
                        .HandleTimeDomainEvent(
                            uart.WriteChar, value, when);
                }
            }
        }

        [PostDeserialization]
        private void ReattachAfterDeserialization()
        {
            lock(locker)
            {
                foreach(var uart in uarts)
                {
                    uart.Key.CharReceived += uart.Value;
                }
            }
        }

        private bool started;
        private readonly Dictionary<IUART, Action<byte>>
            uarts;
        private readonly object locker;

        private const int MaxConnections = 2;

        private static readonly TimeInterval CharDelay =
            TimeInterval.FromMicroseconds(100);
    }
}
