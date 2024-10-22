//
// Copyright (c) 2024 Auxon (jon@auxon.io)
//
//  This file is licensed under the MIT License.
//  Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Time;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;

namespace Antmicro.Renode.Peripherals.Analog
{
    public class S32K3XX_ADC : BasicDoubleWordPeripheral, IKnownSize
    {
        public S32K3XX_ADC(IMachine machine) : base(machine)
        {
            precisionChannels = Enumerable.Range(0, NumberOfPrecisionChannels).Select(x => new ADCChannel(this, x)).ToArray();
            standardChannels = Enumerable.Range(0, NumberOfStandardChannels).Select(x => new ADCChannel(this, x)).ToArray();

            DefineRegisters();

            // Sampling time fixed
            /*
            samplingTimer = new LimitTimer(
            machine.ClockSource, 1000000, this, "samplingClock",
            limit: 100,
            eventEnabled: true,
            direction: Direction.Ascending,
            enabled: false,
            autoUpdate: false,
            workMode: WorkMode.OneShot);
            samplingTimer.LimitReached += OnConversionFinished;
            */
        }

        public void FeedSample(uint value, uint channelIdx, int repeat = 1)
        {
            /*
            if(IsValidChannel(channelIdx))
            {
            channels[channelIdx].FeedSample(value, repeat);
            }
            */
        }

        public void FeedSample(string path, uint channelIdx, int repeat = 1)
        {
            /*
            if(IsValidChannel(channelIdx))
            {
            var parsedSamples = ADCChannel.ParseSamplesFile(path);
            channels[channelIdx].FeedSample(parsedSamples, repeat);
            }
            */
        }

        public override void Reset()
        {
            base.Reset();
            /*
            foreach(var c in channels)
            {
            c.Reset();
            }
            */
        }

        public long Size => 0x400;

        public GPIO IRQ { get; } = new GPIO();
        public GPIO DMARequest { get; } = new GPIO();

        private void DefineRegisters()
        {
            Registers.MainConfiguration.Define(this, 0x00000001, name: "MCR")
                .WithFlag(0, out poweredDown,
                    writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            state.Value = AdcState.PowerDown;
                        }
                        else
                        {
                            state.Value = AdcState.Idle;
                        }
                    },
                    name: "PWDN")
                .WithTag("ADCLKSEL", 1, 2)
                .WithReservedBits(3, 2)
                .WithTaggedFlag("ACKO", 5)
                .WithTaggedFlag("ABORT", 6)
                .WithTaggedFlag("ABORTCHAIN", 7)
                .WithReservedBits(8, 1)
                .WithTaggedFlag("AVGEN", 11)
                .WithReservedBits(12, 3)
                .WithTaggedFlag("STCL", 15)
                .WithTaggedFlag("BCTU_MODE", 16)
                .WithTaggedFlag("BCTUEN", 17)
                .WithReservedBits(18, 2)
                .WithTaggedFlag("JSTART", 20)
                .WithTaggedFlag("JEDGE", 21)
                .WithTaggedFlag("JTRGEN", 22)
                .WithReservedBits(23, 1)
                .WithFlag(24,
                    name: "NSTART",
                    writeCallback: (_, value) => { if(value) StartConversion(); },
                    valueProviderCallback: _ => false)
                .WithTaggedFlag("XSTRTEN", 25)
                .WithTaggedFlag("EDGE", 26)
                .WithTaggedFlag("TRGEN", 27)
                .WithReservedBits(28, 1)
                .WithTaggedFlag("MODE", 29)
                .WithTaggedFlag("WLSIDE", 30)
                .WithTaggedFlag("OWREN", 31);
            
            Registers.MainStatus.Define(this, 0x00000001, name: "MSR")
                .WithEnumField<DoubleWordRegister, AdcState>(0, 3, out state, FieldMode.Read, name: "ADCSTATUS")
                .WithReservedBits(3, 2)
                .WithTaggedFlag("ACKO", 5)
                .WithReservedBits(6, 3)
                .WithTag("CHADDR", 9, 7)
                .WithTaggedFlag("BCTUSTART", 16)
                .WithReservedBits(17, 1)
                .WithTaggedFlag("SELF_TEST_S", 18)
                .WithReservedBits(19, 1)
                .WithTaggedFlag("JSTART", 20)
                .WithReservedBits(21, 2)
                .WithTaggedFlag("JABORT", 23)
                .WithTaggedFlag("NSTART", 24)
                .WithReservedBits(25, 6)
                .WithTaggedFlag("CALIBRTD", 31);

            foreach (var index in Enumerable.Range(0, NumberOfPrecisionChannels))
            {
                // TODO always valid currently
                var offset = index * 4;
                (Registers.PrecisionConversionData0 + offset).Define(this)
                    .WithValueField(0, 16, FieldMode.Read,
                            valueProviderCallback: _ => precisionChannels[index].GetSample(),
                            name: $"CDATA (PCDR{index})")
                    .WithTag($"RESULT (PCDR{index})", 16, 2)
                    .WithTaggedFlag($"OVERW (PCDR{index})", 18)
                    .WithFlag(19, FieldMode.Read, valueProviderCallback: _ => true, name: $"VALID (PCDR{index})")
                    .WithReservedBits(20, 12);
            }

            foreach (var index in Enumerable.Range(0, NumberOfStandardChannels))
            {
                // TODO always valid currently
                var offset = index * 4;
                (Registers.StandardConversionData0 + offset).Define(this)
                    .WithValueField(0, 16, FieldMode.Read,
                            valueProviderCallback: _ => standardChannels[index].GetSample(),
                            name: $"CDATA (ICDR{index})")
                    .WithTag($"RESULT (ICDR{index})", 16, 2)
                    .WithTaggedFlag($"OVERW (ICDR{index})", 18)
                    .WithFlag(19, FieldMode.Read, valueProviderCallback: _ => true, name: $"VALID (ICDR{index})")
                    .WithReservedBits(20, 12);
            }
        }

        private void StartConversion()
        {
            // TODO start timer/etc
        }

        private IFlagRegisterField poweredDown;
        private IEnumRegisterField<AdcState> state;
      
        private readonly ADCChannel[] precisionChannels;
        private readonly ADCChannel[] standardChannels;
        // TODO support external channels

        // TODO this gives each ADC the same number of channels, when in reality
        // this is just what ADC0/1 have
        public const int NumberOfPrecisionChannels = 8;
        public const int NumberOfStandardChannels = 16;
        //public const int NumberOfExternalChannels = 32;

        private enum AdcState
        {
            Idle = 0,
            PowerDown = 1,
            Wait = 2,
            Calibrate = 3,
            Convert = 4,
            Done = 6,
        }

        private enum Registers
        {
            MainConfiguration = 0x00,           // MCR
            MainStatus = 0x04,                  // MSR
            PrecisionConversionData0 = 0x100,   // PCDR0
            StandardConversionData0 = 0x180,    // ICDR0
        }
    }
}
