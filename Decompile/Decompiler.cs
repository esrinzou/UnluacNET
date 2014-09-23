﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UnluacNET
{
    public class Decompiler
    {
        private static Stack<Branch> m_backup;

        private readonly int m_stackSize;
        private readonly int m_length;
        private readonly Upvalues m_upvalues;

        private readonly LFunction[] m_functions;
        private readonly int m_params;
        private readonly int m_vararg;

        private readonly Op m_tForTarget;

        private Registers m_registers;
        private Block m_outer;

        private List<Block> m_blocks;

        private bool[] m_skip;
        private bool[] m_reverseTarget;

        public Code Code { get; private set; }
        public Declaration[] DeclList { get; private set; }

        // TODO: Pick better names
        protected Function InputChunk { get; set; }
        protected LFunction Function { get; set; }

        public void Decompile()
        {
            m_registers = new Registers(m_stackSize, m_length, DeclList, InputChunk);

            FindReverseTargets();
            HandleBranches(true);

            m_outer = HandleBranches(false);

            ProcessSequence(1, m_length);
        }

        private int BreakTarget(int line)
        {
            var tLine = Int32.MaxValue;

            foreach (var block in m_blocks)
            {
                if (block.Breakable && block.Contains(line))
                    tLine = Math.Min(tLine, block.End);
            }

            if (tLine == Int32.MaxValue)
                return -1;

            return tLine;
        }

        private Block EnclosingBlock(int line)
        {
            //Assumes the outer block is first
            var outer = m_blocks[0];
            var enclosing = outer;

            for (int i = 1; i < m_blocks.Count; i++)
            {
                var next = m_blocks[i];

                if (next.IsContainer && enclosing.Contains(next) && next.Contains(line) && !next.LoopRedirectAdjustment)
                    enclosing = next;
            }

            return enclosing;
        }

        private Block EnclosingBlock(Block block)
        {
            //Assumes the outer block is first
            var outer = m_blocks[0];
            var enclosing = outer;

            for (int i = 1; i < m_blocks.Count; i++)
            {
                var next = m_blocks[i];

                if (next == block)
                    continue;

                if (next.Contains(block) && enclosing.Contains(next))
                    enclosing = next;
            }

            return enclosing;
        }

        private Block EnclosingBreakableBlock(int line)
        {
            var outer = m_blocks[0];
            var enclosing = outer;

            for (int i = 1; i < m_blocks.Count; i++)
            {
                var next = m_blocks[i];

                if (enclosing.Contains(next) && next.Contains(line) && next.Breakable && !next.LoopRedirectAdjustment)
                    enclosing = next;
            }

            return enclosing == outer ? null : enclosing;
        }

        private Block EnclosingUnprotectedBlock(int line)
        {
            //Assumes the outer block is first
            var outer = m_blocks[0];
            var enclosing = outer;

            for (int i = 1; i < m_blocks.Count; i++)
            {
                var next = m_blocks[i];

                if (enclosing.Contains(next) && next.Contains(line) && next.IsUnprotected && !next.LoopRedirectAdjustment)
                    enclosing = next;
            }

            return enclosing == outer ? null : enclosing;
        }

        private void FindReverseTargets()
        {
            m_reverseTarget = new bool[m_length + 1];

            for (int line = 1; line <= m_length; line++)
            {
                var sBx = Code.sBx(line);

                if (Code.Op(line) == Op.JMP && sBx < 0)
                    m_reverseTarget[line + 1 + sBx] = true;
            }
        }

        private int GetAssignment(int line)
        {
            switch (Code.Op(line))
            {
            case Op.MOVE:
            case Op.LOADK:
            case Op.LOADBOOL:
            case Op.GETUPVAL:
            case Op.GETTABUP:
            case Op.GETGLOBAL:
            case Op.GETTABLE:
            case Op.NEWTABLE:
            case Op.ADD:
            case Op.SUB:
            case Op.MUL:
            case Op.DIV:
            case Op.MOD:
            case Op.POW:
            case Op.UNM:
            case Op.NOT:
            case Op.LEN:
            case Op.CONCAT:
            case Op.CLOSURE:
                return Code.A(line);

            case Op.LOADNIL:
                return (Code.A(line) == Code.B(line)) ? Code.A(line) : -1;

            case Op.SETGLOBAL:
            case Op.SETUPVAL:
            case Op.SETTABUP:
            case Op.SETTABLE:
            case Op.JMP:
            case Op.TAILCALL:
            case Op.RETURN:
            case Op.FORLOOP:
            case Op.FORPREP:
            case Op.TFORCALL:
            case Op.TFORLOOP:
            case Op.CLOSE:
                return -1;

            case Op.SELF:
                return -1;

            case Op.EQ:
            case Op.LT:
            case Op.LE:
            case Op.TEST:
            case Op.TESTSET:
            case Op.SETLIST:
                return -1;

            case Op.CALL:
                return (Code.C(line) == 2) ? Code.A(line) : -1;

            case Op.VARARG:
                return (Code.C(line) == 2) ? Code.B(line) : -1;

            default:
                throw new InvalidOperationException("Illegal opcode: " + Code.Op(line));
            }
        }

        private Target GetMoveIntoTargetTarget(int line, int previous)
        {
            switch (Code.Op(line))
            {
            case Op.MOVE:
                return m_registers.GetTarget(Code.A(line), line);
            case Op.SETUPVAL:
                return new UpvalueTarget(m_upvalues.GetName(Code.B(line)));
            case Op.SETGLOBAL:
                return new GlobalTarget(InputChunk.GetGlobalName(Code.Bx(line)));
            case Op.SETTABLE:
                return new TableTarget(
                    m_registers.GetExpression(Code.A(line), previous),
                    m_registers.GetKExpression(Code.B(line), previous));
            default:
                throw new InvalidOperationException();
            }
        }

        private Expression GetMoveIntoTargetValue(int line, int previous)
        {
            var A = Code.A(line);
            var B = Code.B(line);
            var C = Code.C(line);

            switch (Code.Op(line))
            {
            case Op.MOVE:
                return m_registers.GetValue(B, previous);
            case Op.SETUPVAL:
            case Op.SETGLOBAL:
                return m_registers.GetExpression(A, previous);
            case Op.SETTABLE:
                {
                    if ((C & 0x100) != 0)
                        throw new InvalidOperationException();

                    return m_registers.GetExpression(C, previous);
                }
            default:
                throw new InvalidOperationException();
            }
        }

        // TODO: Optimize / rewrite method
        private OuterBlock HandleBranches(bool first)
        {
            var oldBlocks = m_blocks;

            m_blocks = new List<Block>();

            var outer = new OuterBlock(Function, m_length);

            m_blocks.Add(outer);

            var isBreak = new bool[m_length + 1];
            var loopRemoved = new bool[m_length + 1];

            if (!first)
            {
                foreach (var block in oldBlocks)
                {
                    if (block is AlwaysLoop)
                        m_blocks.Add(block);
                    if (block is Break)
                    {
                        m_blocks.Add(block);
                        isBreak[block.Begin] = true;
                    }
                }

                var delete = new LinkedList<Block>();

                foreach (var block in m_blocks)
                {
                    if (block is AlwaysLoop)
                    {
                        foreach (var block2 in m_blocks)
                        {
                            if (block != block2 && block.Begin == block2.Begin)
                            {
                                if (block.End < block2.End)
                                {
                                    delete.AddLast(block);
                                    loopRemoved[block.End - 1] = true;
                                }
                                else
                                {
                                    delete.AddLast(block2);
                                    loopRemoved[block2.End - 1] = true;
                                }
                            }
                        }
                    }
                }

                foreach (var block in delete)
                    m_blocks.Remove(block);
            }

            m_skip = new bool[m_length + 1];

            var stack = new Stack<Branch>();

            var reduce = false;
            var testSet = false;
            var testSetEnd = -1;

            for (int line = 1; line <= m_length; line++)
            {
                if (!m_skip[line])
                {
                    var A = Code.A(line);
                    var B = Code.B(line);
                    var C = Code.C(line);
                    var Bx = Code.Bx(line);
                    var sBx = Code.sBx(line);
                    
                    var op = Code.Op(line);
                    
                    switch (op)
                    {
                    case Op.EQ:
                    case Op.LT:
                    case Op.LE:
                        {
                            Branch node = null;

                            // TODO: Optimize nodes
                            switch (op)
                            {
                            case Op.EQ:
                                node = new EQNode(
                                    B,
                                    C,
                                    A != 0,
                                    line,
                                    line + 2,
                                    line + 2 + Code.sBx(line + 1));
                                break;
                            case Op.LT:
                                node = new LTNode(
                                B,
                                C,
                                A != 0,
                                line,
                                line + 2,
                                line + 2 + Code.sBx(line + 1));
                                break;
                            case Op.LE:
                                node = new LENode(
                                B,
                                C,
                                A != 0,
                                line,
                                line + 2,
                                line + 2 + Code.sBx(line + 1));
                                break;
                            }

                            stack.Push(node);

                            m_skip[line + 1] = true;

                            if (Code.Op(node.End) == Op.LOADBOOL)
                            {
                                if (Code.C(node.End) != 0)
                                {
                                    node.IsCompareSet = true;
                                    node.SetTarget = Code.A(node.End);
                                }
                                else if (Code.Op(node.End - 1) == Op.LOADBOOL)
                                {
                                    if (Code.C(node.End - 1) != 0)
                                    {
                                        node.IsCompareSet = true;
                                        node.SetTarget = Code.A(node.End);
                                    }
                                }
                            }
                        } continue;
                    case Op.TEST:
                        {
                            stack.Push(new TestNode(
                                A,
                                C != 0,
                                line,
                                line + 2,
                                line + 2 + Code.sBx(line + 1)));

                            m_skip[line + 1] = true;
                        } continue;
                    case Op.TESTSET:
                        {
                            testSet = true;
                            testSetEnd = line + 2 + Code.sBx(line + 1);

                            stack.Push(new TestSetNode(
                                A,
                                B,
                                C != 0,
                                line,
                                line + 2,
                                line + 2 + Code.sBx(line + 1)));

                            m_skip[line + 1] = true;
                        } continue;
                    case Op.JMP:
                        {
                            reduce = true;
                            var tLine = line + 1 + Code.sBx(line);

                            if (tLine >= 2 &&
                                Code.Op(tLine - 1) == Op.LOADBOOL &&
                                Code.C(tLine - 1) != 0)
                            {
                                stack.Push(new TrueNode(
                                    Code.A(tLine - 1),
                                    false,
                                    line,
                                    line + 1,
                                    tLine));

                                m_skip[line + 1] = true;
                            }
                            else if (Code.Op(tLine) == m_tForTarget && !m_skip[tLine])
                            {
                                var tA = Code.A(tLine);
                                var tC = Code.C(tLine);

                                if (tC == 0)
                                    throw new InvalidOperationException();

                                m_registers.SetInternalLoopVariable(tA, tLine, line + 1); // TODO: end?
                                m_registers.SetInternalLoopVariable(tA + 1, tLine, line + 1);
                                m_registers.SetInternalLoopVariable(tA + 2, tLine, line + 1);

                                for (int index = 1; index <= tC; index++)
                                {
                                    m_registers.SetExplicitLoopVariable(
                                        tA + 2 + index,
                                        line,
                                        tLine + 2); // TODO: end?
                                }

                                m_skip[tLine] = true;
                                m_skip[tLine + 1] = true;

                                m_blocks.Add(new TForBlock(
                                    Function,
                                    line + 1,
                                    tLine + 2,
                                    tA,
                                    tC,
                                    m_registers));
                            }
                            else if (Code.sBx(line) == 2 &&
                                Code.Op(line + 1) == Op.LOADBOOL &&
                                Code.C(line + 1) != 0)
                            {
                                /* This is the tail of a boolean set with a compare node and assign node */
                                m_blocks.Add(new BooleanIndicator(Function, line));
                            }
                            else
                            {
                                if (first || loopRemoved[line])
                                {
                                    if (tLine > line)
                                    {
                                        isBreak[line] = true;
                                        m_blocks.Add(new Break(Function, line, tLine));
                                    }
                                    else
                                    {
                                        var enclosing = EnclosingBreakableBlock(line);

                                        if (enclosing != null &&
                                            enclosing.Breakable &&
                                            (Code.Op(enclosing.End) == Op.JMP) &&
                                            (Code.sBx(enclosing.End) + enclosing.End + 1 == tLine))
                                        {
                                            isBreak[line] = true;
                                            m_blocks.Add(new Break(Function, line, enclosing.End));
                                        }
                                        else
                                        {
                                            m_blocks.Add(new AlwaysLoop(Function, tLine, line + 1));
                                        }
                                    }
                                }
                            }
                        } break;
                    case Op.FORPREP:
                        {
                            reduce = true;

                            m_blocks.Add(new ForBlock(
                                Function,
                                line + 1,
                                line + 2 + sBx,
                                A,
                                m_registers));

                            m_skip[line + 1 + sBx] = true;

                            m_registers.SetInternalLoopVariable(A, line, line + 2 + sBx);
                            m_registers.SetInternalLoopVariable(A + 1, line, line + 2 + sBx);
                            m_registers.SetInternalLoopVariable(A + 2, line, line + 2 + sBx);
                            
                            m_registers.SetExplicitLoopVariable(A + 3, line, line + 2 + sBx);
                        } break;
                    case Op.FORLOOP:
                        // Should be skipped by preceding FORPREP
                        throw new InvalidOperationException();
                    default:
                        reduce = IsStatement(line);
                        break;
                    }
                }

                if (((line + 1) <= m_length && m_reverseTarget[line + 1]) ||
                    testSet && testSetEnd == line + 1)
                {
                    reduce = true;
                }

                if (stack.Count == 0)
                    reduce = false;

                if (reduce)
                {
                    reduce = false;

                    var conditions = new Stack<Branch>();
                    var backups = new Stack<Stack<Branch>>();

                    do
                    {
                        var peekNode = stack.Peek();
                        var isAssignNode = peekNode is TestSetNode;
                        var assignEnd = peekNode.End;

                        var compareCorrect = false;

                        if (peekNode is TrueNode)
                        {
                            isAssignNode = true;
                            compareCorrect = true;

                            assignEnd += (Code.C(assignEnd) != 0) ? 2 : 1;
                        }
                        else if (peekNode.IsCompareSet)
                        {
                            if (Code.Op(peekNode.Begin) != Op.LOADBOOL ||
                                Code.C(peekNode.Begin) != 0)
                            {
                                isAssignNode = true;
                                assignEnd += (Code.C(assignEnd) != 0) ? 2 : 1;

                                compareCorrect = true;
                            }
                        }
                        else if (assignEnd - 3 >= 1 &&
                            Code.Op(assignEnd - 2) == Op.LOADBOOL &&
                            Code.C(assignEnd - 2) != 0 &&
                            Code.Op(assignEnd - 3) == Op.JMP &&
                            Code.sBx(assignEnd - 3) == 2)
                        {
                            if (peekNode is TestNode)
                            {
                                var node = peekNode as TestNode;

                                if (node.Test == Code.A(assignEnd - 2))
                                    isAssignNode = true;
                            }
                        }
                        else if (assignEnd - 2 >= 1 &&
                            Code.Op(assignEnd - 1) == Op.LOADBOOL &&
                            Code.C(assignEnd - 1) != 0 &&
                            Code.Op(assignEnd - 2) == Op.JMP &&
                            Code.sBx(assignEnd - 2) == 2)
                        {
                            if (peekNode is TestNode)
                            {
                                isAssignNode = true;
                                assignEnd += 1;
                            }
                        }
                        else if (assignEnd - 1 >= 1 &&
                            Code.Op(assignEnd) == Op.LOADBOOL &&
                            Code.C(assignEnd) != 0 &&
                            Code.Op(assignEnd - 1) == Op.JMP &&
                            Code.sBx(assignEnd - 1) == 2)
                        {
                            if (peekNode is TestNode)
                            {
                                isAssignNode = true;
                                assignEnd += 2;
                            }
                        }
                        else if (assignEnd - 1 >= 1 &&
                            m_registers.IsLocal(GetAssignment(assignEnd - 1), assignEnd - 1) &&
                            assignEnd > peekNode.Line)
                        {
                            var decl = m_registers.GetDeclaration(GetAssignment(assignEnd - 1), assignEnd - 1);

                            if (decl.Begin == assignEnd - 1 && decl.End > assignEnd - 1)
                                isAssignNode = true;
                        }

                        if (!compareCorrect &&
                            assignEnd - 1 == peekNode.Begin &&
                            Code.Op(peekNode.Begin) == Op.LOADBOOL &&
                            Code.C(peekNode.Begin) != 0)
                        {
                            m_backup = null;
                            
                            var begin = peekNode.Begin;
                            var target = Code.A(begin);

                            assignEnd = begin + 2;

                            var condition = PopCompareSetCondition(stack, assignEnd);

                            condition.SetTarget = target;
                            condition.End = assignEnd;
                            condition.Begin = begin;

                            conditions.Push(condition);
                        }
                        else if (isAssignNode)
                        {
                            m_backup = null;

                            var begin = peekNode.Begin;
                            var target = peekNode.SetTarget;

                            var condition = PopSetCondition(stack, assignEnd);

                            condition.SetTarget = target;
                            condition.End = assignEnd;
                            condition.Begin = begin;

                            conditions.Push(condition);
                        }
                        else
                        {
                            m_backup = new Stack<Branch>();

                            conditions.Push(PopCondition(stack));

                            m_backup.Reverse();
                        }

                        backups.Push(m_backup);

                    } while (!(stack.Count == 0));

                    do
                    {
                        var cond = conditions.Pop();
                        var backup = backups.Pop();

                        var breakTarget = BreakTarget(cond.Begin);
                        var breakable = (breakTarget >= 1);

                        if (breakable && Code.Op(breakTarget) == Op.JMP)
                            breakTarget += (1 + Code.sBx(breakTarget));

                        if (breakable && breakTarget == cond.End)
                        {
                            var immediateEnclosing = EnclosingBlock(cond.Begin);

                            for (int iline = Math.Max(cond.End, immediateEnclosing.End - 1); iline >= Math.Max(cond.Begin, immediateEnclosing.Begin); iline--)
                            {
                                if (Code.Op(iline) == Op.JMP &&
                                    iline + 1 + Code.sBx(iline) == breakTarget)
                                {
                                    cond.End = iline;
                                    break;
                                }
                            }
                        }

                        /* A branch has a tail if the instruction just before the end target is JMP */
                        var hasTail = cond.End >= 2 && Code.Op(cond.End - 1) == Op.JMP;


                        /* This is the target of the tail JMP */
                        var tail = hasTail ? (cond.End + Code.sBx(cond.End - 1)) & 0x1FFFF : -1;
                        var originalTail = tail;

                        var enclosing = EnclosingUnprotectedBlock(cond.Begin);

                        /* Checking enclosing unprotected block to undo JMP redirects. */
                        if (enclosing != null)
                        {
                            if (enclosing.GetLoopback() == cond.End)
                            {
                                cond.End = enclosing.End - 1;

                                hasTail = cond.End >= 2 && Code.Op(cond.End - 1) == Op.JMP;
                                tail = hasTail ? cond.End + Code.sBx(cond.End - 1) : -1;
                            }

                            if (hasTail && enclosing.GetLoopback() == tail)
                                tail = enclosing.End - 1;
                        }

                        if (cond.IsSet)
                        {
                            var empty = cond.Begin == cond.End;

                            if (Code.Op(cond.Begin) == Op.JMP &&
                                Code.sBx(cond.Begin) == 2 &&
                                Code.Op(cond.Begin + 1) == Op.LOADBOOL &&
                                Code.C(cond.Begin + 1) != 0)
                            {
                                empty = true;
                            }

                            m_blocks.Add(new SetBlock(Function, cond, cond.SetTarget, line, cond.Begin, cond.End, empty, m_registers));
                        }
                        else if (Code.Op(cond.Begin) == Op.LOADBOOL && Code.C(cond.Begin) != 0)
                        {
                            var begin = cond.Begin;
                            var target = Code.A(begin);

                            if (Code.B(begin) == 0)
                                cond = cond.Invert();

                            m_blocks.Add(new CompareBlock(Function, begin, begin + 2, target, cond));
                        }
                        else if (cond.End < cond.Begin)
                        {
                            m_blocks.Add(new RepeatBlock(Function, cond, m_registers));
                        }
                        else if (hasTail)
                        {
                            var endOp = Code.Op(cond.End - 2);

                            var isEndCondJump = endOp == Op.EQ || endOp == Op.LE || endOp == Op.LT || endOp == Op.TEST || endOp == Op.TESTSET;

                            if (tail > cond.End || (tail == cond.End && !isEndCondJump))
                            {
                                var op = Code.Op(tail - 1);
                                var sbx = Code.sBx(tail - 1);

                                var loopback2 = tail + sbx;

                                var isBreakableLoopEnd = Function.Header.Version.IsBreakableLoopEnd(op);

                                if (isBreakableLoopEnd && loopback2 <= cond.Begin && !isBreak[tail - 1])
                                {
                                    /* (ends with break) */
                                    m_blocks.Add(new IfThenEndBlock(Function, cond, backup, m_registers));
                                }
                                else
                                {
                                    m_skip[cond.End - 1] = true; // Skip the JMP over the else block

                                    var emptyElse = tail == cond.End;

                                    m_blocks.Add(new IfThenElseBlock(Function, cond, originalTail, emptyElse, m_registers));

                                    if (!emptyElse)
                                        m_blocks.Add(new ElseEndBlock(Function, cond.End, tail));

                                }
                            }
                            else
                            {
                                var loopback = tail;
                                var existsStatement = false;

                                for (int sl = loopback; sl < cond.Begin; sl++)
                                {
                                    if (!m_skip[sl] && IsStatement(sl))
                                    {
                                        existsStatement = true;
                                        break;
                                    }
                                }

                                //TODO: check for 5.2-style if cond then break end
                                if (loopback >= cond.Begin || existsStatement)
                                {
                                    m_blocks.Add(new IfThenEndBlock(Function, cond, backup, m_registers));
                                }
                                else
                                {
                                    m_skip[cond.End - 1] = true;
                                    m_blocks.Add(new WhileBlock(Function, cond, originalTail, m_registers));
                                }
                            }
                        }
                        else
                        {
                            m_blocks.Add(new IfThenEndBlock(Function, cond, backup, m_registers));
                        }

                    } while (!(conditions.Count == 0));
                }
            }

            //Find variables whose scope isn't controlled by existing blocks:
            foreach (var decl in DeclList)
            {
                if (!decl.ForLoop && !decl.ForLoopExplicit)
                {
                    var needsDoEnd = true;

                    foreach (var block in m_blocks)
                    {
                        if (block.Contains(decl.Begin) &&
                            (block.ScopeEnd == decl.End))
                        {
                            needsDoEnd = false;
                            break;
                        }
                    }

                    if (needsDoEnd)
                    {
                        //Without accounting for the order of declarations, we might
                        //create another do..end block later that would eliminate the
                        //need for this one. But order of decls should fix this.
                        m_blocks.Add(new DoEndBlock(Function, decl.Begin, decl.End + 1));
                    }
                }
            }

            // Remove breaks that were later parsed as else jumps
            var newBlocks = new List<Block>();

            foreach (var block in m_blocks)
            {
                if (m_skip[block.Begin] && block is Break)
                    continue;

                newBlocks.Add(block);
            }

            m_blocks = newBlocks;
            m_backup = null;

            return outer;
        }

        private bool IsMoveIntoTarget(int line)
        {
            switch (Code.Op(line))
            {
            case Op.MOVE:
                return m_registers.IsAssignable(Code.A(line), line) &&
                    !m_registers.IsLocal(Code.B(line), line);

            case Op.SETUPVAL:
            case Op.SETGLOBAL:
                return !m_registers.IsLocal(Code.A(line), line);

            case Op.SETTABLE:
                {
                    var c = Code.C(line);
                    return (c & 0x100) != 0 ? false : !m_registers.IsLocal(c, line);
                }

            default:
                return false;
            }
        }

        private bool IsStatement(int line)
        {
            return IsStatement(line, -1);
        }

        private bool IsStatement(int line, int testRegister)
        {
            switch (Code.Op(line))
            {
            case Op.MOVE:
            case Op.LOADK:
            case Op.LOADBOOL:
            case Op.GETUPVAL:
            case Op.GETTABUP:
            case Op.GETGLOBAL:
            case Op.GETTABLE:
            case Op.NEWTABLE:
            case Op.ADD:
            case Op.SUB:
            case Op.MUL:
            case Op.DIV:
            case Op.MOD:
            case Op.POW:
            case Op.UNM:
            case Op.NOT:
            case Op.LEN:
            case Op.CONCAT:
            case Op.CLOSURE:
                return m_registers.IsLocal(Code.A(line), line) || Code.A(line) == testRegister;

            case Op.LOADNIL:
                for (int register = Code.A(line); register <= Code.B(line); register++)
                {
                    if (m_registers.IsLocal(register, line))
                        return true;
                }

                return false;

            case Op.SETGLOBAL:
            case Op.SETUPVAL:
            case Op.SETTABUP:
            case Op.SETTABLE:
            case Op.JMP:
            case Op.TAILCALL:
            case Op.RETURN:
            case Op.FORLOOP:
            case Op.FORPREP:
            case Op.TFORCALL:
            case Op.TFORLOOP:
            case Op.CLOSE:
                return true;
            case Op.SELF:
                {
                    var a = Code.A(line);
                    return m_registers.IsLocal(a, line) || m_registers.IsLocal(a + 1, line);
                }
            case Op.EQ:
            case Op.LT:
            case Op.LE:
            case Op.TEST:
            case Op.TESTSET:
            case Op.SETLIST:
                return false;
            case Op.CALL:
                {
                    int a = Code.A(line);
                    int c = Code.C(line);

                    if (c == 1)
                        return true;

                    if (c == 0)
                        c = m_stackSize - a + 1;

                    for (int register = a; register < a + c - 1; register++)
                    {
                        if (m_registers.IsLocal(register, line))
                            return true;
                    }

                    return (c == 2 && a == testRegister);
                }
            case Op.VARARG:
                {
                    int a = Code.A(line);
                    int b = Code.B(line);
                    
                    if (b == 0)
                        b = m_stackSize - a + 1;
                    
                    for (int register = a; register < a + b - 1; register++)
                    {
                        if (m_registers.IsLocal(register, line))
                            return true;
                    }

                    return false;
                }
            default:
                throw new InvalidOperationException("Illegal opcode: " + Code.Op(line));
            }
        }

        private void HandleInitialDeclares(Output output)
        {
            var initDecls = new List<Declaration>(DeclList.Length);

            for (int i = m_params + (m_vararg & 1); i < DeclList.Length; i++)
            {
                var decl = DeclList[i];

                if (decl.Begin == 0)
                    initDecls.Add(decl);
            }

            if (initDecls.Count > 0)
            {
                output.Print("local ");
                output.Print(initDecls[0].Name);

                for (int i = 1; i < initDecls.Count; i++)
                {
                    output.Print(", ");
                    output.Print(initDecls[i].Name);
                }

                output.PrintLine();
            }
        }

        private LinkedList<Operation> ProcessLine(int line)
        {
            var operations = new LinkedList<Operation>();

            var A = Code.A(line);
            var B = Code.B(line);
            var C = Code.C(line);
            var Bx = Code.Bx(line);

            switch (Code.Op(line))
            {
            case Op.MOVE:
                operations.AddLast(new RegisterSet(line, A, m_registers.GetExpression(B, line)));
                break;
            case Op.LOADK:
                operations.AddLast(new RegisterSet(line, A, InputChunk.GetConstantExpression(Bx)));
                break;
            case Op.LOADBOOL:
                {
                    var constant = new Constant((B != 0) ? LBoolean.LTRUE : LBoolean.LFALSE);

                    operations.AddLast(new RegisterSet(line, A, new ConstantExpression(constant, -1)));
                } break;
            case Op.LOADNIL:
                {
                    var maximum = (Function.Header.Version.UsesOldLoadNilEncoding) ? B : (A + B);

                    while (A <= maximum)
                    {
                        operations.AddLast(new RegisterSet(line, A, Expression.NIL));
                        A++;
                    }
                } break;
            case Op.GETUPVAL:
                operations.AddLast(new RegisterSet(line, A, m_upvalues.GetExpression(B)));
                break;
            case Op.GETTABUP:
                {
                    var expr = (B == 0 && (C & 0x100) != 0)
                        ? InputChunk.GetGlobalExpression(C & 0xFF) as Expression
                        : new TableReference(m_upvalues.GetExpression(B), m_registers.GetKExpression(C, line)) as Expression;

                    operations.AddLast(new RegisterSet(line, A, expr));
                } break;
            case Op.GETGLOBAL:
                operations.AddLast(new RegisterSet(line, A, InputChunk.GetGlobalExpression(Bx)));
                break;
            case Op.GETTABLE:
                operations.AddLast(new RegisterSet(line, A, new TableReference(m_registers.GetExpression(B, line), m_registers.GetKExpression(C, line))));
                break;
            case Op.SETUPVAL:
                operations.AddLast(new UpvalueSet(line, m_upvalues.GetName(B), m_registers.GetExpression(A, line)));
                break;
            case Op.SETTABUP:
                if (A == 0 && (B & 0x100) != 0)
                {
                    //TODO: check
                    operations.AddLast(new GlobalSet(line, InputChunk.GetGlobalName(B & 0xFF), m_registers.GetKExpression(C, line)));
                }
                else
                {
                    operations.AddLast(new TableSet(line, m_upvalues.GetExpression(A), m_registers.GetKExpression(B, line), m_registers.GetKExpression(C, line), true, line));
                }
                break;
            case Op.SETGLOBAL:
                operations.AddLast(new GlobalSet(line, InputChunk.GetGlobalName(Bx), m_registers.GetExpression(A, line)));
                break;
            case Op.SETTABLE:
                operations.AddLast(new TableSet(line, m_registers.GetExpression(A, line), m_registers.GetKExpression(B, line), m_registers.GetKExpression(C, line), true, line));
                break;
            case Op.NEWTABLE:
                operations.AddLast(new RegisterSet(line, A, new TableLiteral(B, C)));
                break;

            case Op.SELF:
                {
                    // We can later determine is : syntax was used by comparing subexpressions with ==
                    var common = m_registers.GetExpression(B, line);

                    operations.AddLast(new RegisterSet(line, A + 1, common));
                    operations.AddLast(new RegisterSet(line, A, new TableReference(common, m_registers.GetKExpression(C, line))));
                } break;

            case Op.ADD:
                operations.AddLast(new RegisterSet(line, A, Expression.MakeADD(m_registers.GetKExpression(B, line), m_registers.GetKExpression(C, line))));
                break;
            case Op.SUB:
                operations.AddLast(new RegisterSet(line, A, Expression.MakeSUB(m_registers.GetKExpression(B, line), m_registers.GetKExpression(C, line))));
                break;
            case Op.MUL:
                operations.AddLast(new RegisterSet(line, A, Expression.MakeMUL(m_registers.GetKExpression(B, line), m_registers.GetKExpression(C, line))));
                break;
            case Op.DIV:
                operations.AddLast(new RegisterSet(line, A, Expression.MakeDIV(m_registers.GetKExpression(B, line), m_registers.GetKExpression(C, line))));
                break;
            case Op.MOD:
                operations.AddLast(new RegisterSet(line, A, Expression.MakeMOD(m_registers.GetKExpression(B, line), m_registers.GetKExpression(C, line))));
                break;
            case Op.POW:
                operations.AddLast(new RegisterSet(line, A, Expression.MakePOW(m_registers.GetKExpression(B, line), m_registers.GetKExpression(C, line))));
                break;
            case Op.UNM:
                operations.AddLast(new RegisterSet(line, A, Expression.MakeUNM(m_registers.GetExpression(B, line))));
                break;
            case Op.NOT:
                operations.AddLast(new RegisterSet(line, A, Expression.MakeNOT(m_registers.GetExpression(B, line))));
                break;
            case Op.LEN:
                operations.AddLast(new RegisterSet(line, A, Expression.MakeLEN(m_registers.GetExpression(B, line))));
                break;

            case Op.CONCAT:
                {
                    var value = m_registers.GetExpression(C, line);

                    //Remember that CONCAT is right associative.
                    while (C-- > B)
                        value = Expression.MakeCONCAT(m_registers.GetExpression(C, line), value);

                    operations.AddLast(new RegisterSet(line, A, value));
                } break;

            case Op.JMP:
            case Op.EQ:
            case Op.LT:
            case Op.LE:
            case Op.TEST:
            case Op.TESTSET:
                /* Do nothing ... handled with branches */
                break;

            case Op.CALL:
                {
                    var multiple = (C >= 3 || C == 0);

                    if (B == 0)
                        B = m_stackSize - A;

                    if (C == 0)
                        C = m_stackSize - A + 1;

                    var function = m_registers.GetExpression(A, line);
                    var arguments = new Expression[B - 1];

                    for (int register = A + 1; register <= A + B - 1; register++)
                        arguments[register - A - 1] = m_registers.GetExpression(register, line);

                    var value = new FunctionCall(function, arguments, multiple);

                    if (C == 1)
                    {
                        operations.AddLast(new CallOperation(line, value));
                    }
                    else
                    {
                        if (C == 2 && !multiple)
                        {
                            operations.AddLast(new RegisterSet(line, A, value));
                        }
                        else
                        {
                            for (int register = A; register <= A + C - 2; register++)
                                operations.AddLast(new RegisterSet(line, register, value));
                        }
                    }
                } break;

            case Op.TAILCALL:
                {
                    if (B == 0)
                        B = m_stackSize - A;

                    var function = m_registers.GetExpression(A, line);
                    var arguments = new Expression[B - 1];

                    for (int register = A + 1; register <= A + B - 1; register++)
                        arguments[register - A - 1] = m_registers.GetExpression(register, line);

                    var value = new FunctionCall(function, arguments, true);

                    operations.AddLast(new ReturnOperation(line, value));

                    m_skip[line + 1] = true;
                } break;

            case Op.RETURN:
                {
                    if (B == 0)
                        B = m_stackSize - A + 1;

                    var values = new Expression[B - 1];

                    for (int register = A; register <= A + B - 2; register++)
                        values[register - A] = m_registers.GetExpression(register, line);

                    operations.AddLast(new ReturnOperation(line, values));
                } break;

            case Op.FORLOOP:
            case Op.FORPREP:
            case Op.TFORCALL:
            case Op.TFORLOOP:
                /* Do nothing ... handled with branches */
                break;

            case Op.SETLIST:
                {
                    if (C == 0)
                    {
                        C = Code.CodePoint(line + 1);

                        m_skip[line + 1] = true;
                    }

                    if (B == 0)
                        B = m_stackSize - A - 1;

                    var table = m_registers.GetValue(A, line);

                    for (int i = 1; i <= B; i++)
                        operations.AddLast(new TableSet(line, table, new ConstantExpression(new Constant((C - 1) * 50 + i), -1), m_registers.GetExpression(A + i, line), false, m_registers.GetUpdated(A + i, line)));
                } break;
            case Op.CLOSE:
                break;
            case Op.CLOSURE:
                {
                    var f = m_functions[Bx];

                    operations.AddLast(new RegisterSet(line, A, new ClosureExpression(f, DeclList, line + 1)));
                    
                    if (Function.Header.Version.UsesInlineUpvalueDeclaritions)
                    {
                        // Skip upvalue declarations
                        for (int i = 0; i < f.NumUpValues; i++)
                            m_skip[line + 1 + i] = true;
                    }
                } break;
            case Op.VARARG:
                {
                    var multiple = (B != 2);

                    if (B == 1)
                        throw new InvalidOperationException();

                    if (B == 0)
                        B = m_stackSize - A + 1;

                    var value = new Vararg(B - 1, multiple);

                    for (int register = A; register <= A + B - 2; register++)
                        operations.AddLast(new RegisterSet(line, register, value));
                } break;
            default:
                throw new InvalidOperationException("Illegal instruction: " + Code.Op(line));
            }

            return operations;
        }

        private Assignment ProcessOperation(Operation operation, int line, int nextLine, Block block)
        {
            Assignment assign = null;
            var wasMultiple = false;

            var stmt = operation.Process(m_registers, block);

            // TODO: Optimize code
            if (stmt != null)
            {
                if (stmt is Assignment)
                {
                    assign = stmt as Assignment;

                    if (!assign.GetFirstValue().IsMultiple)
                        block.AddStatement(stmt);
                    else
                        wasMultiple = true;
                }
                else
                {
                    block.AddStatement(stmt);
                }

                if (assign != null)
                {
                    while (nextLine < block.End && IsMoveIntoTarget(nextLine))
                    {
                        var target = GetMoveIntoTargetTarget(nextLine, line + 1);
                        var value = GetMoveIntoTargetValue(nextLine, line + 1); // updated?

                        assign.AddFirst(target, value);

                        m_skip[nextLine] = true;

                        nextLine++;
                    }

                    if (wasMultiple && !assign.GetFirstValue().IsMultiple)
                        block.AddStatement(stmt);
                }
            }

            return assign;
        }

        private void ProcessSequence(int begin, int end)
        {
            var blockIndex = 1;
            var blockStack = new Stack<Block>();

            blockStack.Push(m_blocks[0]);

            m_skip = new bool[end + 1];

            for (int line = begin; line <= end; line++)
            {
                Operation blockHandler = null;

                while (blockStack.Peek().End <= line)
                {
                    var b = blockStack.Pop();
                    
                    blockHandler = b.Process(this);

                    if (blockHandler != null)
                        break;
                }

                if (blockHandler == null)
                {
                    while (blockIndex < m_blocks.Count && m_blocks[blockIndex].Begin <= line)
                        blockStack.Push(m_blocks[blockIndex++]);
                }

                var block = blockStack.Peek();

                m_registers.StartLine(line); // Must occur AFTER block.rewrite (???)

                if (m_skip[line])
                {
                    var nLocals = m_registers.GetNewLocals(line);

                    if (!(nLocals.Count == 0))
                    {
                        var a = new Assignment();

                        a.Declare(nLocals[0].Begin);

                        foreach (var decl in nLocals)
                            a.AddLast(new VariableTarget(decl), m_registers.GetValue(decl.Register, line));

                        blockStack.Peek().AddStatement(a);
                    }

                    continue;
                }

                var operations = ProcessLine(line);
                var newLocals = m_registers.GetNewLocals(blockHandler == null ? line : line - 1);

                Assignment assign = null;

                if (blockHandler == null)
                {
                    if (Code.Op(line) == Op.LOADNIL)
                    {
                        assign = new Assignment();

                        var count = 0;

                        foreach (var operation in operations)
                        {
                            var set = operation as RegisterSet;
                            operation.Process(m_registers, block);

                            if (m_registers.IsAssignable(set.Register, set.Line))
                            {
                                assign.AddLast(m_registers.GetTarget(set.Register, set.Line), set.Value);
                                count++;
                            }
                        }

                        if (count > 0)
                            block.AddStatement(assign);
                    }
                    else
                    {
                        foreach (var operation in operations)
                        {
                            var temp = ProcessOperation(operation, line, line + 1, block);

                            if (temp != null)
                                assign = temp;
                        }

                        if (assign != null && assign.GetFirstValue().IsMultiple)
                            block.AddStatement(assign);
                    }
                }
                else
                {
                    assign = ProcessOperation(blockHandler, line, line, block);
                }

                if (assign != null)
                {
                    if (!(newLocals.Count == 0))
                    {
                        assign.Declare(newLocals[0].Begin);

                        foreach (var decl in newLocals)
                            assign.AddLast(new VariableTarget(decl), m_registers.GetValue(decl.Register, line + 1));
                    }
                }

                if (blockHandler == null)
                {
                    if (assign != null)
                    {
                        // TODO: Handle when 'blockHandler' is null and 'assign' is NOT null
                    }
                    else if (!(newLocals.Count == 0) && Code.Op(line) != Op.FORPREP)
                    {
                        if (Code.Op(line) != Op.JMP || Code.Op(line + 1 + Code.sBx(line)) != m_tForTarget)
                        {
                            assign = new Assignment();
                            assign.Declare(newLocals[0].Begin);

                            foreach (var decl in newLocals)
                                assign.AddLast(new VariableTarget(decl), m_registers.GetValue(decl.Register, line));

                            blockStack.Peek().AddStatement(assign);
                        }
                    }
                }
                else
                {
                    line--;
                    continue;
                }
            }
        }

        public Branch PopCondition(Stack<Branch> stack)
        {
            var branch = stack.Pop();

            if (m_backup != null)
                m_backup.Push(branch);

            if (branch is TestSetNode)
                throw new InvalidOperationException();

            var begin = branch.Begin;

            if (Code.Op(branch.Begin) == Op.JMP)
                begin += (1 + Code.sBx(branch.Begin));

            while (!(stack.Count == 0))
            {
                var next = stack.Peek();

                if (next is TestSetNode)
                    break;

                if (next.End == begin)
                    branch = new OrBranch(PopCondition(stack).Invert(), branch);
                else if (next.End == branch.End)
                    branch = new AndBranch(PopCondition(stack), branch);
                else
                    break;
            }

            return branch;
        }

        public Branch PopCompareSetCondition(Stack<Branch> stack, int assignEnd)
        {
            var top = stack.Pop();
            var invert = false;

            if (Code.B(top.Begin) == 0)
                invert = true;

            top.Begin = assignEnd;
            top.End = assignEnd;

            stack.Push(top);

            // Invert argument doesn't matter because begin == end
            return PopSetConditionInternal(stack, invert, assignEnd);
        }

        public Branch PopSetCondition(Stack<Branch> stack, int assignEnd)
        {
            stack.Push(new AssignNode(assignEnd - 1, assignEnd, assignEnd));
            
            //Invert argument doesn't matter because begin == end
            return PopSetConditionInternal(stack, false, assignEnd);
        }

        private Branch PopSetConditionInternal(Stack<Branch> stack, bool invert, int assignEnd)
        {
            var branch = stack.Pop();

            var begin = branch.Begin;
            var end = branch.End;

            if (invert)
                branch = branch.Invert();

            if (Code.Op(begin) == Op.LOADBOOL)
                begin += (Code.C(begin) != 0) ? 2 : 1;
            if (Code.Op(end) == Op.LOADBOOL)
                end += (Code.C(end) != 0) ? 2 : 1;

            var target = branch.SetTarget;

            while (!(stack.Count == 0))
            {
                var next = stack.Peek();
                var nInvert = false;
                var nEnd = next.End;

                if (Code.Op(nEnd) == Op.LOADBOOL)
                {
                    nInvert = Code.B(nEnd) != 0;
                    nEnd += (Code.C(nEnd) != 0) ? 2 : 1;
                }
                else if (next is TestNode)
                {
                    // also applies to TestSetNode's
                    nInvert = ((TestNode)next).Inverted;
                }
                else if (nEnd >= assignEnd)
                {
                        break;
                }

                var addr = (nInvert == invert) ? end : begin;

                if (addr == nEnd)
                {
                    // TODO: Fix impossible statement
                    //if (addr != nEnd)
                    //    nInvert = !nInvert;

                    var left = PopSetConditionInternal(stack, nInvert, assignEnd);

                    if (nInvert)
                        branch = new OrBranch(left, branch);
                    else
                        branch = new AndBranch(left, branch);

                    branch.End = nEnd;
                }
                else
                {
                    if (!(branch is TestSetNode))
                    {
                        stack.Push(branch);
                        branch = PopCondition(stack);
                    }

                    break;
                }
            }

            branch.IsSet = true;
            branch.SetTarget = target;

            return branch;
        }

        public void Print()
        {
            Print(new Output());
        }

        public void Print(Output output)
        {
            HandleInitialDeclares(output);
            m_outer.Print(output);
        }

        public Decompiler(LFunction function)
        {
            InputChunk = new Function(function);
            Function = function;

            m_stackSize = function.MaxStackSize;
            m_length = function.Code.Length;

            Code = new Code(function);

            if (function.Locals.Length >= function.NumParams)
            {
                DeclList = new Declaration[function.Locals.Length];

                for (int i = 0; i < DeclList.Length; i++)
                    DeclList[i] = new Declaration(function.Locals[i]);
            }
            else
            {
                // TODO: debug info missing;
                DeclList = new Declaration[function.NumParams];

                for (int i = 0; i < DeclList.Length; i++)
                {
                    var name = String.Format("_ARG_{0}_", i);

                    DeclList[i] = new Declaration(name, 0, m_length - 1);
                }
            }

            m_upvalues = new Upvalues(function.UpValues);
            m_functions = function.Functions;
            m_params = function.NumParams;
            m_vararg = function.VarArg;
            m_tForTarget = function.Header.Version.TForTarget;
        }
    }
}
