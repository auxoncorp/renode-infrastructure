//
// Copyright (c) 2024 Auxon (jon@auxon.io)
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Core;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Peripherals.MemoryControllers;

namespace Antmicro.Renode.Peripherals.MTD
{
    public class C40ASFFlash : BasicDoubleWordPeripheral, IKnownSize
    {
        public C40ASFFlash(IMachine machine, IPFlash pflash, MappedMemory programFlash, MappedMemory dataFlash) : base(machine)
        {
            // Master/Domain ID is fixed currently (FLS_MASTER_ID)
            domainId = 0;

            this.machine = machine;
            this.pflash = pflash;
            this.programFlash = programFlash;
            this.dataFlash = dataFlash;

            programData = new IValueRegisterField[ProgramDataRegisterCount];

            DefineRegisters();
        }

        public override void Reset()
        {
            base.Reset();
            resetProgramData();
        }

        public long Size => 0x400;

        private void DefineRegisters()
        {
            Registers.Config.Define(this, 0x00FF0000, name: "MCR")
                .WithFlag(0, out enableHighVoltageOp,
                        writeCallback: (oldValue, value) => { if(!oldValue && value) doFlashOperation(); },
                        name: "EHV")
                .WithReservedBits(1, 3)
                .WithFlag(4, out erase,
                        writeCallback: (oldValue, value) => { if(oldValue && !value) resetProgramData(); },
                        name: "ERS")
                .WithEnumField<DoubleWordRegister, EraseSize>(5, 1, out eraseSize, name: "ESS")
                .WithReservedBits(6, 2)
                .WithFlag(8, out program,
                        writeCallback: (oldValue, value) => { if(oldValue && !value) resetProgramData(); },
                        name: "PGM")
                .WithReservedBits(9, 3)
                .WithTaggedFlag("WDIE", 12)
                .WithReservedBits(13, 2)
                .WithTaggedFlag("PECIE", 15)
                .WithValueField(16, 8, FieldMode.Read,
                    valueProviderCallback: _ => domainId,
                    name: "PEID")
                .WithReservedBits(24, 8);

            // NOTE: some of these are w1c
            Registers.ConfigStatus.Define(this, 0x0000C100, name: "MCRS")
                .WithTaggedFlag("RE", 0)
                .WithReservedBits(1, 7)
                .WithTaggedFlag("TSPELOCK", 8)
                .WithTaggedFlag("EPEG", 9)
                .WithReservedBits(10, 2)
                .WithTaggedFlag("WDI", 12)
                .WithReservedBits(13, 1)
                .WithTaggedFlag("PEG", 14)
                .WithFlag(15, out hightVoltageOpDone, FieldMode.Read, name: "DONE")
                .WithTaggedFlag("PES", 16)
                .WithTaggedFlag("PEP", 17)
                .WithReservedBits(18, 2)
                .WithTaggedFlag("RWE", 20)
                .WithReservedBits(21, 3)
                .WithTaggedFlag("RRE", 24)
                .WithTaggedFlag("RVE", 25)
                .WithReservedBits(26, 2)
                .WithTaggedFlag("EEE", 28)
                .WithTaggedFlag("AEE", 29)
                .WithTaggedFlag("SBC", 30)
                .WithTaggedFlag("EER", 31);

            // TODO ExtendedConfig read-only, setup the info to match hw (maybe use params)

            Registers.UTest0.Define(this, 0x00000001, name: "UT0")
                .WithTaggedFlag("AIE", 0)
                .WithReservedBits(31, 1);

            Registers.ProgramData0.DefineMany(this, ProgramDataRegisterCount, stepInBytes: 4, resetValue: 0xFFFFFFFF, name: "DATAn", setup: (reg, index) =>
                {
                    reg.WithValueField(0, 32, out programData[index], name: $"PDATA{index}");
                }
            );
        }

        private void resetProgramData()
        {
            for(var i = 0; i < ProgramDataRegisterCount; i++)
            {
                programData[i].Value = 0xFFFFFFFF;
            }
        }

        private void doFlashOperation()
        {
            if(erase.Value && program.Value)
            {
                throw new ArgumentException("C40ASFFlash cannot program and erase in parallel");
            }

            hightVoltageOpDone.Value = false;

            if(erase.Value)
            {
                flashErase();
            }
            else if(program.Value)
            {
                flashProgram();
            }

            hightVoltageOpDone.Value = true;
        }

        private void flashErase()
        {
            // TODO handle block size
            if(eraseSize.Value == EraseSize.Block)
            {
                throw new ArgumentException("C40ASFFlash doesn't support block-erase yet");
            }

            var sectorAddress = this.pflash.ProgramEraseAddress;

            var underlyingMemory = this.programFlash;
            var blockStart = Block0Addr;
            if(sectorAddress >= Block3Addr)
            {
                underlyingMemory = this.dataFlash;
                blockStart = Block3Addr;
            }
            var offset = sectorAddress - blockStart;

            this.Log(LogLevel.Debug, "Erasing {0}-byte sector at 0x{1:X} (0x{2:X})", SectorSize, offset, sectorAddress);

            underlyingMemory.SetRange((long) offset, SectorSize, underlyingMemory.ResetByte);
        }

        private void flashProgram()
        {
            var programAddress = this.pflash.ProgramEraseAddress;
            var underlyingMemory = this.programFlash;
            var blockStart = Block0Addr;
            if(programAddress >= Block3Addr)
            {
                underlyingMemory = this.dataFlash;
                blockStart = Block3Addr;
            }
            var offset = programAddress - blockStart;

            this.Log(LogLevel.Debug, "Programing at 0x{0:X} (0x{1:X})", offset, programAddress);
            for(ulong i = 0; i < ProgramDataRegisterCount; i++)
            {
                if(programData[i].Value != 0xFFFFFFFF)
                {
                    underlyingMemory.WriteDoubleWord((long) (offset + (i * 4)), (uint) programData[i].Value);
                }
            }
        }

        private IFlagRegisterField enableHighVoltageOp;
        private IFlagRegisterField erase;
        private IFlagRegisterField program;
        private IEnumRegisterField<EraseSize> eraseSize;
        private IFlagRegisterField hightVoltageOpDone;
        private IValueRegisterField[] programData;

        private IMachine machine;
        private IPFlash pflash;
        private MappedMemory programFlash;
        private MappedMemory dataFlash;
        private ulong domainId;

        private const uint ProgramDataRegisterCount = 32;

        private const uint SectorSize = 0x2000; // 8K
        private const uint BlockSize = 0x200000; // 2M

        // Code flash
        private const uint Block0Addr = 0x00400000;
        private const uint Block1Addr = 0x00600000;
        private const uint Block2Addr = 0x00800000;

        // Data flash
        private const uint Block3Addr = 0x10000000;

        private enum EraseSize
        {
            Sector = 0,
            Block = 1,
        }

        private enum Registers
        {
            Config = 0x00,                  // MCR
            ConfigStatus = 0x04,            // MCRS
            ExtendedConfig = 0x08,          // MCRE
            UTest0 = 0x94,                  // UT0

            ProgramData0 = 0x100,           // DATA0
        }
    }
}
