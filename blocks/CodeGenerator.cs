﻿/*
    Copyright (C) 2011 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace de4dot.blocks {
	class CodeGenerator {
		MethodBlocks methodBlocks;
		List<Block> blocks = new List<Block>();
		Stack<BlockState> stateStack = new Stack<BlockState>();
		List<ExceptionInfo> exceptions = new List<ExceptionInfo>();
		Dictionary<BaseBlock, bool> visited = new Dictionary<BaseBlock, bool>();

		class BlockState {
			public ScopeBlock scopeBlock;

			public BlockState(ScopeBlock scopeBlock) {
				this.scopeBlock = scopeBlock;
			}
		}

		class ExceptionInfo {
			public int tryStart;
			public int tryEnd;
			public int filterStart;
			public int handlerStart;
			public int handlerEnd;
			public TypeReference catchType;
			public ExceptionHandlerType handlerType;
			public ExceptionInfo(int tryStart, int tryEnd, int filterStart,
				int handlerStart, int handlerEnd, TypeReference catchType,
				ExceptionHandlerType handlerType) {
				if (tryStart > tryEnd || filterStart > handlerStart || handlerStart > handlerEnd ||
					tryStart < 0 || tryEnd < 0 || filterStart < 0 || handlerStart < 0 || handlerEnd < 0)
					throw new ApplicationException("Invalid start/end/filter/handler indexes");
				this.tryStart = tryStart;
				this.tryEnd = tryEnd;
				this.filterStart = filterStart == handlerStart ? -1 : filterStart;
				this.handlerStart = handlerStart;
				this.handlerEnd = handlerEnd;
				this.catchType = catchType;
				this.handlerType = handlerType;
			}
		}

		public CodeGenerator(MethodBlocks methodBlocks) {
			this.methodBlocks = methodBlocks;
		}

		public void getCode(out IList<Instruction> allInstructions, out IList<ExceptionHandler> allExceptionHandlers) {
			fixEmptyBlocks();
			layOutBlocks();
			sortExceptions();
			layOutInstructions(out allInstructions, out allExceptionHandlers);

			foreach (var instr in allInstructions) {
				if (instr.OpCode == OpCodes.Br_S) instr.OpCode = OpCodes.Br;
				else if (instr.OpCode == OpCodes.Brfalse_S) instr.OpCode = OpCodes.Brfalse;
				else if (instr.OpCode == OpCodes.Brtrue_S) instr.OpCode = OpCodes.Brtrue;
				else if (instr.OpCode == OpCodes.Beq_S) instr.OpCode = OpCodes.Beq;
				else if (instr.OpCode == OpCodes.Bge_S) instr.OpCode = OpCodes.Bge;
				else if (instr.OpCode == OpCodes.Bgt_S) instr.OpCode = OpCodes.Bgt;
				else if (instr.OpCode == OpCodes.Ble_S) instr.OpCode = OpCodes.Ble;
				else if (instr.OpCode == OpCodes.Blt_S) instr.OpCode = OpCodes.Blt;
				else if (instr.OpCode == OpCodes.Bne_Un_S) instr.OpCode = OpCodes.Bne_Un;
				else if (instr.OpCode == OpCodes.Bge_Un_S) instr.OpCode = OpCodes.Bge_Un;
				else if (instr.OpCode == OpCodes.Bgt_Un_S) instr.OpCode = OpCodes.Bgt_Un;
				else if (instr.OpCode == OpCodes.Ble_Un_S) instr.OpCode = OpCodes.Ble_Un;
				else if (instr.OpCode == OpCodes.Blt_Un_S) instr.OpCode = OpCodes.Blt_Un;
				else if (instr.OpCode == OpCodes.Leave_S) instr.OpCode = OpCodes.Leave;
			}

			for (int i = 0; i < 10; i++) {
				if (!optimizeBranches(allInstructions))
					break;
			}

			recalculateInstructionOffsets(allInstructions);
		}

		void recalculateInstructionOffsets(IList<Instruction> allInstructions) {
			int offset = 0;
			foreach (var instr in allInstructions) {
				instr.Offset = offset;
				offset += instr.GetSize();
			}
		}

		bool getShortBranch(Instruction instruction, out OpCode opcode) {
			switch (instruction.OpCode.Code) {
			case Code.Br:		opcode = OpCodes.Br_S; return true;
			case Code.Brfalse:	opcode = OpCodes.Brfalse_S; return true;
			case Code.Brtrue:	opcode = OpCodes.Brtrue_S; return true;
			case Code.Beq:		opcode = OpCodes.Beq_S; return true;
			case Code.Bge:		opcode = OpCodes.Bge_S; return true;
			case Code.Bgt:		opcode = OpCodes.Bgt_S; return true;
			case Code.Ble:		opcode = OpCodes.Ble_S; return true;
			case Code.Blt:		opcode = OpCodes.Blt_S; return true;
			case Code.Bne_Un:	opcode = OpCodes.Bne_Un_S; return true;
			case Code.Bge_Un:	opcode = OpCodes.Bge_Un_S; return true;
			case Code.Bgt_Un:	opcode = OpCodes.Bgt_Un_S; return true;
			case Code.Ble_Un:	opcode = OpCodes.Ble_Un_S; return true;
			case Code.Blt_Un:	opcode = OpCodes.Blt_Un_S; return true;
			case Code.Leave:	opcode = OpCodes.Leave_S; return true;
			default:			opcode = OpCodes.Nop; return false;
			}
		}

		// Returns true if something was changed
		bool optimizeBranches(IList<Instruction> allInstructions) {
			bool changed = false;

			recalculateInstructionOffsets(allInstructions);
			for (int i = 0; i < allInstructions.Count; i++) {
				var instruction = allInstructions[i];
				OpCode opcode;
				if (getShortBranch(instruction, out opcode)) {
					const int instrSize = 5;	// It's a long branch instruction
					var target = (Instruction)instruction.Operand;
					int distance = target.Offset - (instruction.Offset + instrSize);
					if (-0x80 <= distance && distance <= 0x7F) {
						instruction.OpCode = opcode;
						changed = true;
					}
				}
			}

			return changed;
		}

		class BlockInfo {
			public int start;
			public int end;
			public BlockInfo(int start, int end) {
				this.start = start;
				this.end = end;
			}
		}

		void layOutInstructions(out IList<Instruction> allInstructions, out IList<ExceptionHandler> allExceptionHandlers) {
			allInstructions = new List<Instruction>();
			allExceptionHandlers = new List<ExceptionHandler>();

			var blockInfos = new List<BlockInfo>();
			for (int i = 0; i < blocks.Count; i++) {
				var block = blocks[i];

				int startIndex = allInstructions.Count;
				for (int j = 0; j < block.Instructions.Count - 1; j++)
					allInstructions.Add(block.Instructions[j].Instruction);

				if (block.Targets != null) {
					var targets = new List<Instr>();
					foreach (var target in block.Targets)
						targets.Add(target.FirstInstr);
					block.LastInstr.updateTargets(targets);
				}
				allInstructions.Add(block.LastInstr.Instruction);

				var next = i + 1 < blocks.Count ? blocks[i + 1] : null;

				// If eg. ble next, then change it to bgt XYZ and fall through to next.
				if (block.Targets != null && block.canFlipConditionalBranch() && block.Targets[0] == next) {
					block.flipConditionalBranch();
					block.LastInstr.updateTargets(new List<Instr> { block.Targets[0].FirstInstr });
				}
				else if (block.FallThrough != null && block.FallThrough != next) {
					var instr = new Instr(Instruction.Create(OpCodes.Br, block.FallThrough.FirstInstr.Instruction));
					instr.updateTargets(new List<Instr> { block.FallThrough.FirstInstr });
					allInstructions.Add(instr.Instruction);
				}

				int endIndex = allInstructions.Count - 1;

				blockInfos.Add(new BlockInfo(startIndex, endIndex));
			}

			foreach (var ex in exceptions) {
				var tryStart = blockInfos[ex.tryStart].start;
				var tryEnd = blockInfos[ex.tryEnd].end;
				var filterStart = ex.filterStart == -1 ? -1 : blockInfos[ex.filterStart].start;
				var handlerStart = blockInfos[ex.handlerStart].start;
				var handlerEnd = blockInfos[ex.handlerEnd].end;

				var eh = new ExceptionHandler(ex.handlerType);
				eh.CatchType = ex.catchType;
				eh.TryStart = getInstruction(allInstructions, tryStart);
				eh.TryEnd = getInstruction(allInstructions, tryEnd + 1);
				eh.FilterStart = filterStart == -1 ? null : getInstruction(allInstructions, filterStart);
				eh.HandlerStart = getInstruction(allInstructions, handlerStart);
				eh.HandlerEnd = getInstruction(allInstructions, handlerEnd + 1);

				allExceptionHandlers.Add(eh);
			}
		}

		static Instruction getInstruction(IList<Instruction> allInstructions, int i) {
			if (i < allInstructions.Count)
				return allInstructions[i];
			return null;
		}

		void sortExceptions() {
			exceptions.Sort((a, b) => {
				// Make sure nested try blocks are sorted before the outer try block.
				if (a.tryStart > b.tryStart) return -1;	// a could be nested, but b is not
				if (a.tryStart < b.tryStart) return 1;	// b could be nested, but a is not
				// same tryStart
				if (a.tryEnd < b.tryEnd) return -1;		// a is nested
				if (a.tryEnd > b.tryEnd) return 1;		// b is nested
				// same tryEnd (they share try block)

				int ai = a.filterStart == -1 ? a.handlerStart : a.filterStart;
				int bi = b.filterStart == -1 ? b.handlerStart : b.filterStart;
				if (ai < bi) return -1;
				if (ai > bi) return 1;
				// same start

				// if we're here, they should be identical since handlers can't overlap
				// when they share the try block!
				if (a.handlerEnd < b.handlerEnd) return -1;
				if (a.handlerEnd > b.handlerEnd) return 1;
				// same handler end

				return 0;
			});
		}

		void fixEmptyBlocks() {
			foreach (var block in methodBlocks.getAllBlocks()) {
				if (block.Instructions.Count == 0) {
					block.Instructions.Add(new Instr(Instruction.Create(OpCodes.Nop)));
				}
			}
		}

		// Write all blocks to the blocks list
		void layOutBlocks() {
			if (methodBlocks.BaseBlocks.Count == 0)
				return;

			stateStack.Push(new BlockState(methodBlocks));
			processBaseBlocks(methodBlocks.BaseBlocks, (block) => {
				return block.LastInstr.OpCode == OpCodes.Ret;
			});

			stateStack.Pop();
		}

		void processBaseBlocks(List<BaseBlock> lb, Func<Block, bool> placeLast) {
			var bbs = new List<BaseBlock>();
			int lastIndex = -1;
			for (int i = 0; i < lb.Count; i++) {
				var bb = lb[i];
				var block = bb as Block;
				if (block != null && placeLast(block))
					lastIndex = i;
				bbs.Add(bb);
			}
			if (lastIndex != -1) {
				var block = (Block)bbs[lastIndex];
				bbs.RemoveAt(lastIndex);
				bbs.Add(block);
			}
			foreach (var bb in bbs)
				doBaseBlock(bb);
		}

		// Returns the BaseBlock's ScopeBlock. The return value is either current ScopeBlock,
		// the ScopeBlock one step below current (current one's child), or null.
		ScopeBlock getScopeBlock(BaseBlock bb) {
			BlockState current = stateStack.Peek();

			if (current.scopeBlock.isOurBlockBase(bb))
				return current.scopeBlock;
			return (ScopeBlock)current.scopeBlock.toChild(bb);
		}

		void doBaseBlock(BaseBlock bb) {
			BlockState current = stateStack.Peek();
			ScopeBlock newOne = getScopeBlock(bb);
			if (newOne == null)
				return;		// Not a BaseBlock somewhere inside this ScopeBlock
			if (newOne != current.scopeBlock)
				bb = newOne;

			bool hasVisited;
			if (!visited.TryGetValue(bb, out hasVisited))
				visited[bb] = hasVisited = false;
			if (hasVisited)
				return;
			visited[bb] = true;

			if (bb is Block)
				doBlock(bb as Block);
			else if (bb is TryBlock)
				doTryBlock(bb as TryBlock);
			else if (bb is FilterHandlerBlock)
				doFilterHandlerBlock(bb as FilterHandlerBlock);
			else if (bb is HandlerBlock)
				doHandlerBlock(bb as HandlerBlock);
			else
				throw new ApplicationException("Invalid block found");
		}

		void doBlock(Block block) {
			blocks.Add(block);
		}

		void doTryBlock(TryBlock tryBlock) {
			var tryStart = blocks.Count;
			stateStack.Push(new BlockState(tryBlock));
			processBaseBlocks(tryBlock.BaseBlocks, (block) => {
				return block.LastInstr.OpCode == OpCodes.Leave ||
						block.LastInstr.OpCode == OpCodes.Leave_S;
			});
			stateStack.Pop();
			var tryEnd = blocks.Count - 1;

			if (tryBlock.TryHandlerBlocks.Count == 0)
				throw new ApplicationException("No handler blocks");

			foreach (var handlerBlock in tryBlock.TryHandlerBlocks) {
				visited[handlerBlock] = true;

				stateStack.Push(new BlockState(handlerBlock));

				var filterStart = blocks.Count;
				if (handlerBlock.FilterHandlerBlock.BaseBlocks != null)
					doBaseBlock(handlerBlock.FilterHandlerBlock);

				var handlerStart = blocks.Count;
				doBaseBlock(handlerBlock.HandlerBlock);
				var handlerEnd = blocks.Count - 1;

				exceptions.Add(new ExceptionInfo(tryStart, tryEnd, filterStart, handlerStart, handlerEnd, handlerBlock.CatchType, handlerBlock.HandlerType));

				stateStack.Pop();
			}
		}

		void doFilterHandlerBlock(FilterHandlerBlock filterHandlerBlock) {
			stateStack.Push(new BlockState(filterHandlerBlock));
			processBaseBlocks(filterHandlerBlock.BaseBlocks, (block) => {
				return block.LastInstr.OpCode == OpCodes.Endfilter;	// MUST end with endfilter!
			});
			stateStack.Pop();
		}

		void doHandlerBlock(HandlerBlock handlerBlock) {
			stateStack.Push(new BlockState(handlerBlock));
			processBaseBlocks(handlerBlock.BaseBlocks, (block) => {
				return block.LastInstr.OpCode == OpCodes.Endfinally ||
						block.LastInstr.OpCode == OpCodes.Leave ||
						block.LastInstr.OpCode == OpCodes.Leave_S;
			});
			stateStack.Pop();
		}
	}
}