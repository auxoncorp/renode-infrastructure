//
// Copyright (c) 2024 Jon Lamb (jon@auxon.io)
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.CRC
{
    public class S32K3XX_CRC : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize
    {
        public S32K3XX_CRC()
        {
            var registersMap = new Dictionary<long, DoubleWordRegister>
            {
                {(long)Registers.Data, new DoubleWordRegister(this, DefaultInitialValue)
                    .WithValueField(0, 32, out initialValue,
                        writeCallback: (_, value) =>
                        {
                            if(!writeAsSeed.Value)
                            {
                                // Equivalent for byte and word implemented directly in writeByte and writeWord methods
                                UpdateCRC((uint)value, 4);
                            }
                        },
                        valueProviderCallback: _ =>
                        {
                            if(writeAsSeed.Value)
                            {
                                return 0;
                            }
                            else
                            {
                                return CRC.Value;
                            }
                        },
                        name: "DATA"
                    )
                },
                {(long)Registers.Polynomial, new DoubleWordRegister(this, DefaultPolymonial)
                    .WithValueField(0, 32, out polynomial,
                        FieldMode.Read | FieldMode.Write,
                        name: "GPOLY"
                    )
                    .WithWriteCallback((_, __) => { crcConfigDirty = true; })
                },
                {(long)Registers.Control, new DoubleWordRegister(this)
                    .WithReservedBits(0, 24)
                    .WithEnumField<DoubleWordRegister, PolySize>(24, 1, out polySize, name: "TCRC")
                    .WithFlag(25, out writeAsSeed,
                        writeCallback: (_, value) =>
                        {
                            if(!value)
                            {
                                ReloadCRCConfig();
                            }
                        },
                        valueProviderCallback: _ => writeAsSeed.Value,
                        name: "WAS"
                    )
                    .WithFlag(26, out inverseOutput, name: "FXOR")
                    .WithReservedBits(27, 1)
                    .WithEnumField<DoubleWordRegister, Transpose>(28, 2, out transposeOutputData, name: "TOTR")
                    .WithEnumField<DoubleWordRegister, Transpose>(30, 2, out transposeInputData, name: "TOT")
                    .WithWriteCallback((_, __) => { crcConfigDirty = true; })
                }
            };

            registers = new DoubleWordRegisterCollection(this, registersMap);
        }

        public byte ReadByte(long offset)
        {
            // Only properly aligned reads will be handled correctly here
            return (byte)registers.Read(offset);
        }

        public ushort ReadWord(long offset)
        {
            // Only properly aligned reads will be handled correctly here
            return (ushort)registers.Read(offset);
        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteByte(long offset, byte value)
        {
            if((Registers)offset == Registers.Data)
            {
                UpdateCRC(value, 1);
            }
            else
            {
                this.LogUnhandledWrite(offset, value);
            }
        }

        public void WriteWord(long offset, ushort value)
        {
            if((Registers)offset == Registers.Data)
            {
                UpdateCRC(value, 2);
            }
            else
            {
                this.LogUnhandledWrite(offset, value);
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            registers.Write(offset, value);
        }

        public void Reset()
        {
            registers.Reset();
            ReloadCRCConfig();
        }

        public long Size => 0x400;

        private static int PolySizeToCRCWidth(PolySize poly)
        {
            switch(poly)
            {
                case PolySize.CRC32:
                    return 32;
                case PolySize.CRC16:
                    return 16;
                default:
                    throw new ArgumentException($"Unknown PolySize value: {poly}!");
            }
        }

        private void UpdateCRC(uint value, int bytesCount)
        {
            if(bytesCount != 1)
            {
                throw new ArgumentException("S32K3XX_CRC only supports byte updates");
            }
            // NOTE: when/if other write widths are needed, we'll
            // need to handle additional swapping
            // (See STM32_CRC.cs for example)
            // i.e.: value = BitHelper.ReverseBitsByByte(value);

            CRC.Update(BitHelper.GetBytesFromValue(value, bytesCount));
        }

        private void ReloadCRCConfig()
        {
            var cfgPolySize = polySize?.Value ?? PolySize.CRC32;
            var cfgSeed = (uint) initialValue.Value;
            var cfgPoly = (uint) polynomial.Value;
            if(cfgPolySize == PolySize.CRC16)
            {
                cfgSeed = cfgSeed & 0xFFFF;
                cfgPoly = cfgPoly & 0xFFFF;
            }

            var reflectInput = false;
            if((transposeInputData.Value == Transpose.Bytes) || (transposeInputData.Value == Transpose.BitsAndBytes))
            {
                reflectInput = true;
            }

            var reflectOutput = false;
            if((transposeOutputData.Value == Transpose.Bytes) || (transposeOutputData.Value == Transpose.BitsAndBytes))
            {
                reflectOutput = true;
            }

            /*
            this.Log(LogLevel.Debug, "Reloading configuration poly=0x{0:X} seed=0x{1:X}", cfgPoly, cfgSeed);
            this.Log(LogLevel.Debug, "GPOLY=0x{0:X}", polynomial.Value);
            this.Log(LogLevel.Debug, "SEED=0x{0:X}", initialValue.Value);
            this.Log(LogLevel.Debug, "TCRC={0}", polySize?.Value ?? PolySize.CRC32);
            this.Log(LogLevel.Debug, "FXOR={0}", inverseOutput.Value);
            this.Log(LogLevel.Debug, "TOTR={0}", transposeOutputData.Value);
            this.Log(LogLevel.Debug, "TOT={0}", transposeInputData.Value);
            */

            var config = new CRCConfig(
                cfgPoly,
                PolySizeToCRCWidth(cfgPolySize),
                reflectInput: reflectInput,
                reflectOutput: reflectOutput,
                init: cfgSeed,
                xorOutput: inverseOutput.Value ? 0xFFFFFFFF : 0
            );
            if(crc == null || !config.Equals(crc.Config))
            {
                crc = new CRCEngine(config);
            }
            else
            {
                crc.Reset();
            }
            crcConfigDirty = false;
        }

        private CRCEngine CRC
        {
            get
            {
                if(crc == null || crcConfigDirty)
                {
                    ReloadCRCConfig();
                }
                return crc;
            }
        }

        private IFlagRegisterField writeAsSeed;
        private IFlagRegisterField inverseOutput;
        private IEnumRegisterField<Transpose> transposeInputData;
        private IEnumRegisterField<Transpose> transposeOutputData;
        private IEnumRegisterField<PolySize> polySize;
        private IValueRegisterField initialValue;
        private IValueRegisterField polynomial;

        private bool crcConfigDirty;
        private CRCEngine crc;

        private readonly DoubleWordRegisterCollection registers;

        private const uint DefaultInitialValue = 0xFFFFFFFF;
        private const uint DefaultPolymonial = 0x00001021;

        private enum Transpose
        {
            Disabled,
            Bits,
            BitsAndBytes,
            Bytes,
        }

        private enum Registers : long
        {
            Data = 0x0,
            Polynomial = 0x4,
            Control = 0x8,
        }

        private enum PolySize
        {
            CRC16,
            CRC32,
        }
    }
}
