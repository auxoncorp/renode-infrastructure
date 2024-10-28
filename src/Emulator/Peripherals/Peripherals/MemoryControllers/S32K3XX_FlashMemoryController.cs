//
// Copyright (c) 2024 Auxon (jon@auxon.io)
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.IO;
using System.Linq;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Core;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Miscellaneous;

namespace Antmicro.Renode.Peripherals.MemoryControllers
{
	public interface IPFlash
	{
		ulong ProgramEraseAddress { get; }
	}
}

namespace Antmicro.Renode.Peripherals.MemoryControllers
{
    public class S32K3XX_FlashMemoryController : BasicDoubleWordPeripheral, IKnownSize, IPFlash
    {
        public S32K3XX_FlashMemoryController(IMachine machine) : base(machine)
        {
            DefineRegisters();
        }

        public override void Reset()
        {
            base.Reset();
        }

        public long Size => 0x500;

        public ulong ProgramEraseAddress
        {
            get
            {
                return programEraseLogicalAddress?.Value ?? 0;
            }
        }

        private void DefineRegisters()
        {
            Registers.Config0.Define(this, 0x00000003, name: "PFCR0")
                .WithTaggedFlag("P0_CBFEN", 0)
                .WithTaggedFlag("P0_DBFEN", 1)
                .WithReservedBits(2, 2)
                .WithTaggedFlag("P0_CPFEN", 4)
                .WithTaggedFlag("P0_DPFEN", 5)
                .WithReservedBits(6, 26);
            Registers.Config1.Define(this, 0x00000003, name: "PFCR1")
                .WithTaggedFlag("P1_CBFEN", 0)
                .WithTaggedFlag("P1_DBFEN", 1)
                .WithReservedBits(2, 2)
                .WithTaggedFlag("P1_CPFEN", 4)
                .WithTaggedFlag("P1_DPFEN", 5)
                .WithReservedBits(6, 26);
            Registers.Config2.Define(this, 0x00000003, name: "PFCR2")
                .WithTaggedFlag("P2_CBFEN", 0)
                .WithTaggedFlag("P2_DBFEN", 1)
                .WithReservedBits(2, 2)
                .WithTaggedFlag("P2_CPFEN", 4)
                .WithTaggedFlag("P2_DPFEN", 5)
                .WithReservedBits(6, 26);
            Registers.Config3.Define(this, 0x00000003, name: "PFCR3")
                .WithTaggedFlag("P3_CBFEN", 0)
                .WithTaggedFlag("P3_DBFEN", 1)
                .WithReservedBits(2, 2)
                .WithTaggedFlag("P3_CPFEN", 4)
                .WithTaggedFlag("P3_DPFEN", 5)
                .WithReservedBits(6, 26);
            Registers.Config4.Define(this, 0x00000000, name: "PFCR4")
                .WithTaggedFlag("DERR_SUP", 0)
                .WithTag("BLK4_PS", 1, 3)
                .WithReservedBits(4, 3)
                .WithTaggedFlag("DMEEE", 7)
                .WithReservedBits(8, 24);
            
            Registers.ProgramEraseLogicalAddress.Define(this, 0x00000000, name: "PFCPGM_PEADR_L")
                .WithValueField(0, 32, out programEraseLogicalAddress, name: "PEADR_L");

            // NOTE: the default value is 0x0FFFFFFF (all locked), but we're returning 0 indicated unlocked
            
            Registers.Block0SProgramEraseLock.DefineMany(this, 4, stepInBytes: 4, resetValue: 0, name: "PFCBLKn_SPELOCK", setup: (reg, index) =>
                {
                    reg.WithTag($"SLCK{index}", 0, 32);
                }
            );
            Registers.Block0SSProgramEraseLock.DefineMany(this, 4, stepInBytes: 4, resetValue: 0, name: "PFCBLKn_SSPELOCK", setup: (reg, index) =>
                {
                    reg.WithTag($"SSLCK{index}", 0, 28)
                        .WithReservedBits(28, 4);
                }
            );
        }

        private IValueRegisterField programEraseLogicalAddress;

        private enum Registers
        {
            Config0 = 0x00,                 // PFCR0
            Config1 = 0x04,                 // PFCR1
            Config2 = 0x08,                 // PFCR2
            Config3 = 0x0C,                 // PFCR3
            Config4 = 0x10,                 // PFCR4

            ProgramEraseLogicalAddress = 0x300, // PFCPGM_PEADR_L

            Block0SProgramEraseLock = 0x340,    // PFCBLK0_SPELOCK
            Block1SProgramEraseLock = 0x344,    // PFCBLK1_SPELOCK
            Block2SProgramEraseLock = 0x348,    // PFCBLK2_SPELOCK
            Block3SProgramEraseLock = 0x34C,    // PFCBLK3_SPELOCK
            
            Block0SSProgramEraseLock = 0x35C,   // PFCBLK0_SSPELOCK
            Block1SSProgramEraseLock = 0x360,   // PFCBLK1_SSPELOCK
            Block2SSProgramEraseLock = 0x364,   // PFCBLK2_SSPELOCK
        }
    }
}
