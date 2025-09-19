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
using Antmicro.Renode.Time;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Utilities.RESD;

namespace Antmicro.Renode.Peripherals.Analog
{
    public class S32K3XX_ADC : BasicDoubleWordPeripheral, IKnownSize
    {
        public S32K3XX_ADC(IMachine machine, uint numberOfPrecisionChannels = 8, uint numberOfStandardChannels = 16, uint numberOfExternalChannels = 0, long conversionClockfrequency = 360000000, ulong conversionLimit = 10) : base(machine)
        {
            if(numberOfPrecisionChannels != 8)
            {
                throw new ConstructionException($"{nameof(numberOfPrecisionChannels)} parameter should be set to one of the supported values: {{8}}");
            }
            if((numberOfStandardChannels != 16) && (numberOfStandardChannels != 4))
            {
                throw new ConstructionException($"{nameof(numberOfStandardChannels)} parameter should be set to one of the supported values: {{4, 16}}");
            }
            if((numberOfExternalChannels != 0) && (numberOfExternalChannels != 32))
            {
                throw new ConstructionException($"{nameof(numberOfExternalChannels)} parameter should be set to one of the supported values: {{0, 32}}");
            }

            this.numberOfPrecisionChannels = numberOfPrecisionChannels;
            this.numberOfStandardChannels = numberOfStandardChannels;
            this.numberOfExternalChannels = numberOfExternalChannels;

            rng = EmulationManager.Instance.CurrentEmulation.RandomGenerator;

            precisionChannels = Enumerable.Range(0, (int) numberOfPrecisionChannels).Select(ch => new ADCChannelState(this, rng, PrecisionChannelFirst + ch)).ToArray();
            standardChannels = Enumerable.Range(0, (int) numberOfStandardChannels).Select(ch => new ADCChannelState(this, rng, StandardChannelFirst + ch)).ToArray();
            externalChannels = Enumerable.Range(0, (int) numberOfExternalChannels).Select(ch => new ADCChannelState(this, rng, ExternalChannelFirst + ch)).ToArray();


            endOfChainConversion = new InterruptPair();
            endOfConversion = new InterruptPair();
            endOfInjectedChainConversion = new InterruptPair();
            endOfInjectedConversion = new InterruptPair();
            endOfBCTUConversion = new InterruptPair();

            DefineRegisters();

            conversionTimer = new LimitTimer(
                    machine.ClockSource, conversionClockfrequency, this, "conversionClock",
                    limit: conversionLimit,
                    eventEnabled: true,
                    direction: Direction.Ascending,
                    enabled: false,
                    autoUpdate: false,
                    workMode: WorkMode.OneShot);
            conversionTimer.LimitReached += OnConversionFinished;
        }

        // 16 KB
        public long Size => 0x4000;

        public GPIO IRQ { get; } = new GPIO();
        public GPIO DMARequest { get; } = new GPIO();

        // uV
        public uint DefaultChannelVoltage
        {
            get => (defaultChannelVoltage * ADCChannelState.VoltageSampleDivisor);
            set
            {
                var millivolts = value / ADCChannelState.VoltageSampleDivisor;
                defaultChannelVoltage = millivolts.Clamp((uint) 0, ADCChannelState.MaxVoltage);
            }
        }

        public override void Reset()
        {
            this.Log(LogLevel.Debug, "Reset");
            base.Reset();
            foreach(var c in precisionChannels)
            {
                c.Reset(defaultChannelVoltage);
            }
            foreach(var c in standardChannels)
            {
                c.Reset(defaultChannelVoltage);
            }
            foreach(var c in externalChannels)
            {
                c.Reset(defaultChannelVoltage);
            }
            IRQ.Unset();
        }

        public void DumpState()
        {
            foreach(var c in precisionChannels)
            {
                this.Log(LogLevel.Debug, "Precision: {0}", c);
            }
            foreach(var c in standardChannels)
            {
                this.Log(LogLevel.Debug, "Standard: {0}", c);
            }
            foreach(var c in externalChannels)
            {
                this.Log(LogLevel.Debug, "External: {0}", c);
            }
        }

        public void SetADCValue(int adcChannel, uint microvolts)
        {
            // TODO
            //ref var ch = ref GetChannelState(adcChannel);
            //ref var ch = ref 
            GetChannelState(adcChannel).SetADCValue(microvolts);
        }

        public uint GetADCValue(int adcChannel)
        {
            ref var ch = ref GetChannelState(adcChannel);
            return ch.GetADCValue();
        }

        public void EnableRandomNoise(int adcChannel, uint microvolts)
        {
            ref var ch = ref GetChannelState(adcChannel);
            ch.EnableRandomNoise(microvolts);
        }

        public void DisableRandomNoise(int adcChannel)
        {
            ref var ch = ref GetChannelState(adcChannel);
            ch.DisableRandomNoise();
        }

        public void SetToDefault(int adcChannel)
        {
            ref var ch = ref GetChannelState(adcChannel);
            ch.SetToDefault(defaultChannelVoltage);
        }

        // channel => ADCChannelState[index]
        // 0..=7   => precision 0..=7
        // 32..=48 => standard  0..=15
        // 64..=95 => external  0..=31
        private ref ADCChannelState GetChannelState(int adcChannel)
        {
            if((adcChannel >= PrecisionChannelFirst) && (adcChannel <= PrecisionChannelLast))
            {
                return ref GetPrecisionChannelState(adcChannel - PrecisionChannelFirst);
            }
            else if((adcChannel >= StandardChannelFirst) && (adcChannel <= StandardChannelLast))
            {
                return ref GetStandardChannelState(adcChannel - StandardChannelFirst);
            }
            else if((adcChannel >= ExternalChannelFirst) && (adcChannel <= ExternalChannelLast))
            {
                return ref GetExternalChannelState(adcChannel - ExternalChannelFirst);
            }
            else
            {
                throw new RecoverableException($"Invalid adcChannel: {adcChannel}.");
            }
        }

        private ref ADCChannelState GetPrecisionChannelState(int index)
        {
            return ref GetGenericChannelState("precision", precisionChannels, numberOfPrecisionChannels, index);
        }

        private ref ADCChannelState GetStandardChannelState(int index)
        {
            return ref GetGenericChannelState("standard", standardChannels, numberOfStandardChannels, index);
        }

        private ref ADCChannelState GetExternalChannelState(int index)
        {
            return ref GetGenericChannelState("external", externalChannels, numberOfExternalChannels, index);
        }

        private ref ADCChannelState GetGenericChannelState(string log, ADCChannelState[] channels, uint numChannels, int index)
        {
            if(index < numChannels)
            {
                return ref channels[index];
            }
            else
            {
                throw new RecoverableException($"Invalid {log} ADCChannelState index: {index}.");
            }
        }

        private uint GetRightAlignedValue(uint millivolts)
        {
            var adcValue = (uint)(millivolts * ADCChannelState.MaxValue / ADCChannelState.MaxVoltage);
            //this.Log(LogLevel.Debug, "mV={0} ADC={1} CRD=0x{2:X}", millivolts, adcValue, (adcValue << ADCChannelState.ResolutionShift) & ADCChannelState.ResolutionMask);
            return (adcValue << ADCChannelState.ResolutionShift) & ADCChannelState.ResolutionMask;
        }

        private void StartConversion()
        {
            if(state.Value != AdcState.PowerDown)
            {
                if(state.Value != AdcState.Convert)
                {
                    this.Log(LogLevel.Noisy, "Starting conversion state={0} mode={1} time={2}", state.Value, normalConversionMode.Value, machine.ElapsedVirtualTime.TimeElapsed);
                    state.Value = AdcState.Convert;
                    conversionTimer.Enabled = true;
                }
            }
            else
            {
                this.Log(LogLevel.Warning, "Trying to start conversion while ADC is powered down");
            }
        }

        private void StopConversion()
        {
            if(state.Value != AdcState.PowerDown)
            {
                this.Log(LogLevel.Noisy, "Stopping conversion: time={0}", machine.ElapsedVirtualTime.TimeElapsed);

                conversionTimer.Enabled = false;

                if(state.Value == AdcState.Convert)
                {
                    state.Value = AdcState.Idle;
                }
            }
        }

        private void OnConversionFinished()
        {
            this.Log(LogLevel.Noisy, "OnConversionFinished: time={0}", machine.ElapsedVirtualTime.TimeElapsed);

            state.Value = AdcState.Idle;

            // We mock out the actual channel selection process, so we flip all of the channels to valid
            foreach(var c in precisionChannels)
            {
                c.valid[0].Value = true;
            }
            foreach(var c in standardChannels)
            {
                c.valid[0].Value = true;
            }
            foreach(var c in externalChannels)
            {
                c.valid[0].Value = true;
            }

            // Only support ECH and EOC currently
            endOfChainConversion.cause.Value = true;
            endOfConversion.cause.Value = true;

            UpdateInterrupts();
        }

        private void UpdateInterrupts()
        {
            var flag = false;

            flag |= endOfChainConversion.enable.Value && endOfChainConversion.cause.Value;
            flag |= endOfConversion.enable.Value && endOfConversion.cause.Value;
            flag |= endOfInjectedChainConversion.enable.Value && endOfInjectedChainConversion.cause.Value;
            flag |= endOfInjectedConversion.enable.Value && endOfInjectedConversion.cause.Value;
            flag |= endOfBCTUConversion.enable.Value && endOfBCTUConversion.cause.Value;

            var enable = RegistersCollection.Read((ushort) Registers.InterruptMask);
            var cause = RegistersCollection.Read((ushort) Registers.InterruptStatus);

            if(flag != IRQ.IsSet)
            {
                this.Log(LogLevel.Debug, "Setting IRQ flag to {0} IMR=0x{1:X} ISR=0x{2:X}", flag, enable, cause);
                IRQ.Set(flag);
            }
        }

        private void DefineRegisters()
        {
            Registers.MainConfiguration.Define(this, 0x00000001, name: "MCR")
                .WithFlag(0, out poweredDown,
                    writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            StopConversion();
                            state.Value = AdcState.PowerDown;
                            this.Log(LogLevel.Debug, "Powered down");
                        }
                        else if(state.Value == AdcState.PowerDown)
                        {
                            state.Value = AdcState.Idle;
                            this.Log(LogLevel.Debug, "Powered on");
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
                    writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            StartConversion();
                        }
                        else
                        {
                            StopConversion();
                        }
                    },
                    valueProviderCallback: _ => {
                        if(normalConversionMode.Value == NormalConversionMode.Single)
                        {
                            return false;
                        }
                        else
                        {
                            return !poweredDown.Value;
                        }
                    })
                .WithTaggedFlag("XSTRTEN", 25)
                .WithTaggedFlag("EDGE", 26)
                .WithTaggedFlag("TRGEN", 27)
                .WithReservedBits(28, 1)
                .WithEnumField<DoubleWordRegister, NormalConversionMode>(29, 1, out normalConversionMode, name: "MODE")
                .WithTaggedFlag("WLSIDE", 30)
                .WithTaggedFlag("OWREN", 31);

            // NOTE: W1C
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

            Registers.InterruptStatus.Define(this, name: "ISR")
                .WithFlag(0, out endOfChainConversion.cause, FieldMode.WriteOneToClear, name: "ECH")
                .WithFlag(1, out endOfConversion.cause, FieldMode.WriteOneToClear, name: "EOC")
                .WithFlag(2, out endOfInjectedChainConversion.cause, FieldMode.WriteOneToClear, name: "JECH")
                .WithFlag(3, out endOfInjectedConversion.cause, FieldMode.WriteOneToClear, name: "JEOC")
                .WithFlag(4, out endOfBCTUConversion.cause, FieldMode.WriteOneToClear, name: "EOBCTU")
                .WithReservedBits(5, 27)
                .WithWriteCallback((_, __) => { UpdateInterrupts(); });

            // NOTE: W1C
            Registers.ChannelEndConversionPrecision.Define(this, name: "CEOCFR0")
                .WithTaggedFlag("PIEOCF0", 0)
                .WithTaggedFlag("PIEOCF1", 1)
                .WithTaggedFlag("PIEOCF2", 2)
                .WithTaggedFlag("PIEOCF3", 3)
                .WithTaggedFlag("PIEOCF4", 4)
                .WithTaggedFlag("PIEOCF5", 5)
                .WithTaggedFlag("PIEOCF6", 6)
                .WithTaggedFlag("PIEOCF7", 7)
                .WithReservedBits(8, 24);

            // NOTE: W1C
            Registers.ChannelEndConversionStandard.Define(this, name: "CEOCFR1")
                .WithTaggedFlag("SIEOCF0", 0)
                .WithTaggedFlag("SIEOCF1", 1)
                .WithTaggedFlag("SIEOCF2", 2)
                .WithTaggedFlag("SIEOCF3", 3)
                .WithTaggedFlag("SIEOCF4", 4)
                .WithTaggedFlag("SIEOCF5", 5)
                .WithTaggedFlag("SIEOCF6", 6)
                .WithTaggedFlag("SIEOCF7", 7)
                .WithTaggedFlag("SIEOCF8", 8)
                .WithTaggedFlag("SIEOCF9", 9)
                .WithTaggedFlag("SIEOCF10", 10)
                .WithTaggedFlag("SIEOCF11", 11)
                .WithTaggedFlag("SIEOCF12", 12)
                .WithTaggedFlag("SIEOCF13", 13)
                .WithTaggedFlag("SIEOCF14", 14)
                .WithTaggedFlag("SIEOCF15", 15)
                .WithTaggedFlag("SIEOCF16", 16)
                .WithTaggedFlag("SIEOCF17", 17)
                .WithTaggedFlag("SIEOCF18", 18)
                .WithTaggedFlag("SIEOCF19", 19)
                .WithTaggedFlag("SIEOCF20", 20)
                .WithTaggedFlag("SIEOCF21", 21)
                .WithTaggedFlag("SIEOCF22", 22)
                .WithTaggedFlag("SIEOCF23", 23)
                .WithReservedBits(24, 8);

            Registers.ChannelEndConversionExternal.Define(this, name: "CEOCFR2")
                .WithTag("EIEOCFn", 0, 32);

            Registers.InterruptMask.Define(this, name: "IMR")
                .WithFlag(0, out endOfChainConversion.enable, name: "MSKECH")
                .WithFlag(1, out endOfConversion.enable, name: "MSKEOC")
                .WithFlag(2, out endOfInjectedChainConversion.enable, name: "MSKJECH")
                .WithFlag(3, out endOfInjectedConversion.enable, name: "MSKJEOC")
                .WithFlag(4, out endOfBCTUConversion.enable, name: "MSKEOBCTU")
                .WithReservedBits(5, 27)
                .WithWriteCallback((_, __) => { UpdateInterrupts(); });

            foreach (var index in Enumerable.Range(0, (int) numberOfPrecisionChannels))
            {
                var offset = index * 4;
                (Registers.PrecisionConversionData0 + offset).Define(this)
                    .WithValueField(0, 16, FieldMode.Read,
                            valueProviderCallback: _ => GetRightAlignedValue(GetPrecisionChannelState(index).GetChannelMiilliVolts()),
                            name: $"CDATA (PCDR{index})")
                    .WithTag($"RESULT (PCDR{index})", 16, 2)
                    .WithTaggedFlag($"OVERW (PCDR{index})", 18)
                    .WithFlag(19,
                            out GetPrecisionChannelState(index).valid[0],
                            FieldMode.ReadToClear,
                            name: $"VALID (PCDR{index})")
                    .WithReservedBits(20, 12);
            }

            foreach (var index in Enumerable.Range(0, (int) numberOfStandardChannels))
            {
                var offset = index * 4;
                (Registers.StandardConversionData0 + offset).Define(this)
                    .WithValueField(0, 16, FieldMode.Read,
                            valueProviderCallback: _ => GetRightAlignedValue(GetStandardChannelState(index).GetChannelMiilliVolts()),
                            name: $"CDATA (ICDR{index})")
                    .WithTag($"RESULT (ICDR{index})", 16, 2)
                    .WithTaggedFlag($"OVERW (ICDR{index})", 18)
                    .WithFlag(19,
                            out GetStandardChannelState(index).valid[0],
                            FieldMode.ReadToClear,
                            name: $"VALID (ICDR{index})")
                    .WithReservedBits(20, 12);
            }

            foreach (var index in Enumerable.Range(0, (int) numberOfExternalChannels))
            {
                var offset = index * 4;
                (Registers.ExternalConversionData0 + offset).Define(this)
                    .WithValueField(0, 16, FieldMode.Read,
                            valueProviderCallback: _ => GetRightAlignedValue(GetExternalChannelState(index).GetChannelMiilliVolts()),
                            name: $"CDATA (ECDR{index})")
                    .WithTag($"RESULT (ECDR{index})", 16, 2)
                    .WithTaggedFlag($"OVERW (ECDR{index})", 18)
                    .WithFlag(19,
                            out GetExternalChannelState(index).valid[0],
                            FieldMode.ReadToClear,
                            name: $"VALID (ECDR{index})")
                    .WithReservedBits(20, 12);
            }

            Registers.MiscInOut.Define(this, 0x00000811, name: "AMSIO")
                .WithReservedBits(0, 32);

            Registers.ControlAndCalibrationStatus.Define(this, 0, name: "CALBISTREG")
                .WithTaggedFlag("TEST_EN", 0)
                .WithReservedBits(1, 2)
                .WithTaggedFlag("TEST_FAIL", 3)
                .WithTaggedFlag("AVG_EN", 4)
                .WithTag("NR_SMPL", 5, 2)
                .WithReservedBits(7, 1)
                .WithReservedBits(8, 6)
                .WithTaggedFlag("CALSTFUL", 14)
                .WithTaggedFlag("C_T_BUSY", 15)
                .WithReservedBits(16, 11)
                .WithTag("TSAMP", 27, 2)
                .WithTag("RESN", 29, 3);
        }

        private IFlagRegisterField poweredDown;
        private IEnumRegisterField<AdcState> state;
        private IEnumRegisterField<NormalConversionMode> normalConversionMode;

        private InterruptPair endOfChainConversion;
        private InterruptPair endOfConversion;
        private InterruptPair endOfInjectedChainConversion;
        private InterruptPair endOfInjectedConversion;
        private InterruptPair endOfBCTUConversion;

        private uint numberOfPrecisionChannels;
        private uint numberOfStandardChannels;
        private uint numberOfExternalChannels;

        private readonly ADCChannelState[] precisionChannels;
        private readonly ADCChannelState[] standardChannels;
        private readonly ADCChannelState[] externalChannels;

        private readonly LimitTimer conversionTimer;

        // mV
        private uint defaultChannelVoltage = 0;

        private readonly PseudorandomNumberGenerator rng;

        public const int PrecisionChannelFirst = 0;
        public const int PrecisionChannelLast = 7;
        public const int StandardChannelFirst = 32;
        public const int StandardChannelLast = 47;
        public const int ExternalChannelFirst = 64;
        public const int ExternalChannelLast = 95;

        private class InterruptPair
        {
            public IFlagRegisterField cause;
            public IFlagRegisterField enable;
        }

        private enum NormalConversionMode
        {
            Single = 0,
            Continuous = 1,
        }

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
            MainConfiguration = 0x00,               // MCR
            MainStatus = 0x04,                      // MSR
            InterruptStatus = 0x10,                 // ISR
            ChannelEndConversionPrecision = 0x14,   // CEOCFR0
            ChannelEndConversionStandard = 0x18,    // CEOCFR1
            ChannelEndConversionExternal = 0x1C,    // CEOCFR2
            InterruptMask = 0x20,                   // IMR
            PrecisionConversionData0 = 0x100,       // PCDR0
            StandardConversionData0 = 0x180,        // ICDR0
            ExternalConversionData0 = 0x200,        // ECDR0
            MiscInOut = 0x39C,                      // AMSIO
            ControlAndCalibrationStatus = 0x3A0,    // CALBISTREG
        }

        private struct ADCChannelState
        {
            private readonly IPeripheral parent;
            private readonly PseudorandomNumberGenerator rng;

            // In case we want to debug print something with the channel context
            public int channelId;

            // NOTE: used with FieldMode.ReadToClear
            public IFlagRegisterField[] valid; // PCDRn.VALID

            // mV
            public uint rawVoltage;
            public uint rngNoise;

            public const uint VoltageSampleDivisor = 1000;  // uV to mV
            public const uint MaxVoltage = 3300;            // mV
            public const uint MaxValue = 0x3FFF;            // Saturated 14-bit resolution
            public const uint ResolutionMask = 0x7FFE;
            public const int ResolutionShift = 1;

            public ADCChannelState(IPeripheral parent, PseudorandomNumberGenerator rng, int channelId)
            {
                this.parent = parent;
                this.rng = rng;
                this.channelId = channelId;
                this.valid = new IFlagRegisterField[1];
                this.rawVoltage = 0;
                this.rngNoise = 0;
            }

            public override string ToString()
            {
                return $"Channel = {channelId}, rawVoltage = {rawVoltage}mV, rngNoise = {rngNoise}mV";
            }

            public void Reset(uint defaultChannelVoltage)
            {
                parent.Log(LogLevel.Debug, "Channel {0} reset", channelId);
                SetToDefault(defaultChannelVoltage);
                valid[0].Value = false;
            }

            public void SetToDefault(uint defaultChannelVoltage)
            {
                rawVoltage = defaultChannelVoltage;
                parent.Log(LogLevel.Debug, "Channel {0} set to default {1}mV", channelId, rawVoltage);
            }

            public void SetADCValue(uint microvolts)
            {
                rawVoltage = microvolts / VoltageSampleDivisor;
                parent.Log(LogLevel.Debug, "Channel {0} set to {1}mV", channelId, rawVoltage);
            }

            public uint GetADCValue()
            {
                return rawVoltage * VoltageSampleDivisor;
            }

            public void EnableRandomNoise(uint microvolts)
            {
                rngNoise = microvolts / VoltageSampleDivisor;
            }

            public void DisableRandomNoise()
            {
                rngNoise = 0;
            }

            public uint GetChannelMiilliVolts()
            {
                uint voltage = rawVoltage;

                if(rngNoise != 0)
                {
                    int noiseCfg = (int) rngNoise;
                    var noise = rng.Next(-noiseCfg, noiseCfg);
                    int new_voltage = ((int) voltage) + noise;
                    voltage = (uint) new_voltage.Clamp(0, (int) MaxVoltage);
                }

                if(voltage > MaxVoltage)
                {
                    parent.Log(LogLevel.Warning, "The maximum allowed input voltage is {0}mV. Provided value: {1}mV", MaxVoltage, voltage);
                    return MaxVoltage;
                }

                return voltage;
            }
        }
    }
}
