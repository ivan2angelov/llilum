//
// Copyright (c) Microsoft Corporation.    All rights reserved.
//

//#define GENERATE_ONLY_TYPESYSTEM_AND_FUNCTION_DEFINITIONS

namespace Microsoft.Zelig.Configuration.Environment.Abstractions
{
    using System;
    using System.Collections.Generic;
    using Microsoft.Zelig.LLVM;

    using IR = Microsoft.Zelig.CodeGeneration.IR;
    using TS = Microsoft.Zelig.Runtime.TypeSystem;

    public partial class LLVMCompilationState : IR.ImageBuilders.CompilationState
    {
        public LLVM._BasicBlock GetOrInsertBasicBlock( IR.BasicBlock bb )
        {
            return m_function.GetOrInsertBasicBlock( bb.ToShortString( ) );
        }

        protected override bool EmitCodeForBasicBlock_ShouldSkip( IR.Operator op )
        {
            return base.EmitCodeForBasicBlock_ShouldSkip( op );
        }

        protected override uint EmitCodeForBasicBlock_EstimateMinimumSize( IR.Operator op )
        {
            return sizeof(uint);
        }

        protected override void EmitCodeForBasicBlock_EmitOperator( IR.Operator op )
        {
            throw new Exception( "EmitCodeForBasicBlock_EmitOperator not implemented." );
        }

        protected override void EmitCodeForBasicBlock_FlushOperators( )
        {

        }

        private LLVM._BasicBlock m_basicBlock;
        private List<_Value> m_arguments;
        private List<_Value> m_results;

        private _Value GetValue( IR.Expression exp )
        {
            if( exp is IR.VariableExpression )
            {
                if( !m_localValues.ContainsKey( exp ) )
                {
                    _Value v = m_function.GetLocalStackValue( m_method, m_basicBlock, ( IR.VariableExpression )exp, m_manager );
                    m_localValues[ exp ] = v;
                }

                return m_localValues[ exp ];
            }
            else if( exp is IR.ConstantExpression )
            {
                IR.ConstantExpression ce = ( IR.ConstantExpression )exp;

                if( ce.Value == null )
                {
                    return m_manager.Module.GetNullPointer( m_manager.GetOrInsertType( ce.Type ) );
                }

                if( ce.Type.IsFloatingPoint )
                {
                    if( ce.Value is float )
                        return m_manager.Module.GetFloatConstant( ( float )ce.Value );
                    else
                        return m_manager.Module.GetDoubleConstant( ( double )ce.Value );
                }

                if( ce.Type.IsInteger )
                {
                    if( ce.SizeOfValue == 0 )
                    {
                        throw new System.InvalidOperationException( "Integer constant with 0 bits width." );
                    }

                    ulong uVal;
                    ce.GetAsRawUlong( out uVal );
                    LLVM._Type intType = m_manager.GetOrInsertBasicTypeAsLLVMSingleValueType( ce.Type );
                    return m_manager.Module.GetIntConstant( intType, uVal, ce.Type.IsSigned );
                }

                IR.DataManager.DataDescriptor dd = ce.Value as IR.DataManager.DataDescriptor;
                if( dd != null )
                {
                    return m_manager.GlobalValueFromDataDescriptor( dd );
                }

                throw new Exception( "Constant type not supported." );
            }
            else
            {
                throw new Exception( "Expression type not supported." + exp );
            }
        }

        private void WarnUnimplemented( string msg )
        {
            msg = "|||[!]|||" + msg;

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine( msg );
            Console.ResetColor( );

            //Trick to list in-code missing operators.
            //m_basicBlock.InsertASMString( "; " + msg );
        }

        private void OutputStringInline( string str )
        {
            m_basicBlock.InsertASMString( "####################################################" );
            m_basicBlock.InsertASMString( "# " + str );
            m_basicBlock.InsertASMString( "####################################################" );
        }


        public override void EmitCodeForBasicBlock( IR.BasicBlock bb )
        {
#if GENERATE_ONLY_TYPESYSTEM_AND_FUNCTION_DEFINITIONS
            m_manager.TurnOffCompilationAndValidation( );
            m_function.SetExternalLinkage( );
            m_basicBlock = null;
#else // GENERATE_ONLY_TYPESYSTEM_AND_FUNCTION_DEFINITIONS
            m_basicBlock = GetOrInsertBasicBlock( bb );

            foreach( var op in bb.Operators )
            {
                if ( EmitCodeForBasicBlock_ShouldSkip( op ) )
                {
                    continue;
                }

                TranslateOperator( op, bb );
            }
#endif // GENERATE_ONLY_TYPESYSTEM_AND_FUNCTION_DEFINITIONS
        }

        private void TranslateOperator( IR.Operator op, IR.BasicBlock bb )
        {
            //Load Debug metadata            
            // Miguel: (Hack to remove processor.cs epilogue/prologue debug data)
            if( op.DebugInfo != null && !op.DebugInfo.SrcFileName.EndsWith( "ProcessorARMv7M.cs" ) )
            {
                m_basicBlock.SetDebugInfo( op.DebugInfo.BeginLineNumber, op.DebugInfo.BeginColumn, op.DebugInfo.SrcFileName, m_manager, m_method );
            }

            OutputStringInline( op.ToString( ) );
            //Console.WriteLine( "||| " + op );
            //Console.WriteLine( "DoesNotReadThroughPointerOperands: " + op.Capabilities.HasFlag( IR.Operator.OperatorCapabilities.DoesNotReadThroughPointerOperands ) );

            m_arguments = new List<_Value>( );
            m_results = new List<_Value>( );

            foreach( IR.Expression varExp in op.Arguments )
            {
                if( varExp is IR.PhysicalRegisterExpression )
                {
                    throw new System.InvalidOperationException( "ZELIG TOO NEAR ARM: Op argument in physical register" );
                }
                m_arguments.Add( GetValue( varExp ) );
            }

            foreach( IR.VariableExpression varExp in op.Results )
            {
                if( varExp is IR.PhysicalRegisterExpression )
                {
                    throw new System.InvalidOperationException( "ZELIG TOO NEAR ARM: Op result to physical register" );
                }
                m_results.Add( GetValue( varExp ) );
            }

            //ALU

            if( op is IR.AbstractBinaryOperator )
            {
                Translate_AbstractBinaryOperator( ( IR.AbstractBinaryOperator )op );
            }
            else if( op is IR.AbstractUnaryOperator )
            {
                //Todo: Add unary "Finite" operation, which in fact is "CKfinite" from the checks.
                Translate_AbstractUnaryOperator( ( IR.AbstractUnaryOperator )op );
            }
            //Conversions
            else if( op is IR.ConversionOperator )
            {
                Translate_ConversionOperator( ( IR.ConversionOperator )op );
            }
            else if( op is IR.ConvertOperator )
            {
                Translate_ConvertOperator( ( IR.ConvertOperator )op );
            }
            //Store-Load operators
            else if( op is IR.SingleAssignmentOperator )
            {
                Translate_SingleAssignmentOperator( ( IR.SingleAssignmentOperator )op );
            }
            else if( op is IR.AddressAssignmentOperator )
            {
                Translate_AddressAssignmentOperator( ( IR.AddressAssignmentOperator )op );
            }
            else if( op is IR.InitialValueOperator )
            {
                Translate_InitialValueOperator( ( IR.InitialValueOperator )op );
            }
            else if( op is IR.CompareAndSetOperator )
            {
                Translate_CompareAndSetOperator( ( IR.CompareAndSetOperator )op );
            }
            else if( op is IR.LoadIndirectOperator )
            {
                Translate_LoadIndirectOperator( ( IR.LoadIndirectOperator )op );
            }
            else if( op is IR.StoreIndirectOperator )
            {
                Translate_StoreIndirectOperator( ( IR.StoreIndirectOperator )op );
            }
            else if( op is IR.StoreInstanceFieldOperator )
            {
                Translate_StoreInstanceFieldOperator( ( IR.StoreInstanceFieldOperator )op );
            }
            else if( op is IR.LoadInstanceFieldOperator )
            {
                Translate_LoadInstanceFieldOperator( ( IR.LoadInstanceFieldOperator )op );
            }
            else if( op is IR.LoadInstanceFieldAddressOperator )
            {
                Translate_LoadInstanceFieldAddressOperator( ( IR.LoadInstanceFieldAddressOperator )op );
            }
            else if( op is IR.StoreElementOperator )
            {
                Translate_StoreElementOperator( ( IR.StoreElementOperator )op );
            }
            else if( op is IR.LoadElementOperator )
            {
                Translate_LoadElementOperator( ( IR.LoadElementOperator )op );
            }
            else if( op is IR.LoadElementAddressOperator )
            {
                Translate_LoadElementAddressOperator( ( IR.LoadElementAddressOperator )op );
            }
            //Control flow operators
            else if( op is IR.UnconditionalControlOperator )
            {
                Translate_UnconditionalControlOperator( ( IR.UnconditionalControlOperator )op );
            }
            else if( op is IR.BinaryConditionalControlOperator )
            {
                Translate_BinaryConditionalControlOperator( ( IR.BinaryConditionalControlOperator )op );
            }
            else if( op is IR.CompareConditionalControlOperator )
            {
                Translate_CompareConditionalControlOperator( ( IR.CompareConditionalControlOperator )op );
            }
            else if( op is IR.MultiWayConditionalControlOperator )
            {
                Translate_MultiWayConditionalControlOperator( ( IR.MultiWayConditionalControlOperator )op );
            }
            else if( op is IR.ReturnControlOperator )
            {
                Translate_ReturnControlOperator( ( IR.ReturnControlOperator )op );
            }
            else if( op is IR.LeaveControlOperator )
            {
                Translate_LeaveControlOperator( ( IR.LeaveControlOperator )op );
            }
            else if( op is IR.DeadControlOperator )
            {
                Translate_DeadControlOperator( ( IR.DeadControlOperator )op );
            }
            //Calls
            else if( op is IR.StaticCallOperator )
            {
                Translate_StaticCallOperator( ( IR.StaticCallOperator )op );
            }
            else if( op is IR.InstanceCallOperator )
            {
                Translate_InstanceCallOperator( ( IR.InstanceCallOperator )op );
            }
            else if( op is IR.IndirectCallOperator )
            {
                Translate_IndirectCallOperator( ( IR.IndirectCallOperator )op );
            }
            //Checks
            else if( op is IR.NullCheckOperator )
            {
                //NOT IMPLEMENTED BUT MUTED
            }
            else if( op is IR.OutOfBoundCheckOperator )
            {
                //NOT IMPLEMENTED BUT MUTED
            }
            //Misc
            else if( op is IR.CompilationConstraintsOperator )
            {
                //NOT IMPLEMENTED BUT MUTED
            }
            else if( op is IR.NopOperator )
            {
                Translate_NopOperator( ( IR.NopOperator )op );
            }
            else
            {
                WarnUnimplemented( op.ToString( ) );
            }

        }

        _Value ConvertValueToALUOperableType( _Value val )
        {
            val = m_basicBlock.LoadToImmediate( val );

            // LLVM doesn't accept pointers as operands for arithmetic operations, so convert them to integers.
            if( val.IsPointer )
            {
                _Type intPtrType = m_manager.GetOrInsertType( m_wkt.System_UInt32 );
                return m_basicBlock.InsertPointerToInt( val, intPtrType );
            }

            return val;
        }

        void MatchIntegerLengths( ref _Value valA, ref _Value valB )
        {
            if( valA.IsInteger && valB.IsInteger )
            {
                if( valB.Type.SizeInBits > valA.Type.SizeInBits )
                {
                    valA = m_basicBlock.InsertZExt( valA, valB.Type, valA.Type.SizeInBits );
                }
                else
                {
                    valB = m_basicBlock.InsertZExt( valB, valA.Type, valB.Type.SizeInBits );
                }
            }
        }

        _Value ConvertValueToStoreToTarget( _Value val, LLVM._Type targetType )
        {
            val = m_basicBlock.LoadToImmediate( val );

            if( val.Type.IsPointer && targetType.IsPointer )
            {
                return m_basicBlock.InsertBitCast( val, targetType );
            }

            // Trivial case: Value is already the desired type. We check this *after* pointer-to-pointer conversions
            // because this check can have false positives between pointer types for arrays and strings.
            if( val.Type == targetType )
            {
                return val;
            }

            if( val.Type.IsInteger && targetType.IsInteger )
            {
                if( val.Type.SizeInBits < targetType.SizeInBits )
                {
                    // TODO: Use SExt when val.Type is signed.
                    return m_basicBlock.InsertZExt( val, targetType, val.Type.SizeInBits );
                }

                if( val.Type.SizeInBits > targetType.SizeInBits )
                {
                    return m_basicBlock.InsertTrunc( val, targetType, val.Type.SizeInBits );
                }

                return m_basicBlock.InsertBitCast( val, targetType );
            }

            if( val.Type.IsInteger && targetType.IsPointer )
            {
                return m_basicBlock.InsertIntToPointer( val, targetType );
            }

            if( val.Type.IsPointer && targetType.IsInteger )
            {
                return m_basicBlock.InsertPointerToInt( val, targetType );
            }

            if( val.Type.IsFloatingPoint && targetType.IsFloatingPoint )
            {
                if( val.Type.SizeInBits > targetType.SizeInBits )
                {
                    return m_basicBlock.InsertFPTrunc( val, targetType );
                }
                else
                {
                    return m_basicBlock.InsertFPExt( val, targetType );
                }
            }

            if( val.Type.IsFloatingPoint && targetType.IsInteger )
            {
                return m_basicBlock.InsertFPToInt( val, targetType );
            }

            if( val.Type.IsInteger && targetType.IsFloatingPoint )
            {
                return m_basicBlock.InsertIntToFP( val, targetType );
            }

            throw new InvalidOperationException( "Invalid type cast." );
        }

        void StoreValue( _Value dst, _Value src )
        {
            LLVM._Type underlyingType = dst.Type.UnderlyingType;
            _Value srcVal = m_basicBlock.LoadToImmediate( src );

            // Optimization: If we're copying a struct, use a memset or memcopy instead. This saves on code size.
            if( srcVal.Type.IsStruct && underlyingType.IsStruct && ( srcVal.Type == underlyingType ) )
            {
                if( srcVal.IsZeroedValue( ) )
                {
                    m_basicBlock.InsertMemSet( dst, 0 );
                    return;
                }
                else if( src.IsPointer )
                {
                    m_basicBlock.InsertMemCpy( dst, src );
                    return;
                }
            }

            if( underlyingType.IsPrimitiveType )
            {
                m_basicBlock.InsertStoreIntoBT( dst, ConvertValueToStoreToTarget( srcVal, underlyingType ) );
            }
            else
            {
                m_basicBlock.InsertStore( dst, ConvertValueToStoreToTarget( srcVal, underlyingType ) );
            }
        }

        private void Translate_AbstractBinaryOperator( IR.AbstractBinaryOperator op )
        {
            //Todo: Add support for overflow exceptions

            if( op is IR.BinaryOperator )
            {
                //Todo: add exceptions 
                _Value valA = ConvertValueToALUOperableType( m_arguments[ 0 ] );
                _Value valB = ConvertValueToALUOperableType( m_arguments[ 1 ] );
                MatchIntegerLengths( ref valA, ref valB );
                _Value result = m_basicBlock.InsertBinaryOp( ( int )op.Alu, valA, valB, op.Signed );
                StoreValue( m_results[ 0 ], result );
            }
            else
            {
                throw new Exception( "Unhandled Binary Op: " + op );
            }

        }

        private void Translate_AbstractUnaryOperator( IR.AbstractUnaryOperator op )
        {
            //Todo: Add support for overflow exceptions

            if( op is IR.UnaryOperator )
            {
                _Value val = ConvertValueToALUOperableType( m_arguments[ 0 ] );
                _Value result = m_basicBlock.InsertUnaryOp( ( int )op.Alu, val, op.Signed );
                StoreValue( m_results[ 0 ], result );
            }
            else
            {
                throw new Exception( "Unhandled Unary Op: " + op );
            }
        }

        private void Translate_ConversionOperator( IR.ConversionOperator op )
        {
            _Value v = ConvertValueToALUOperableType( m_arguments[ 0 ] );
            LLVM._Type resultType = m_manager.GetOrInsertBasicTypeAsLLVMSingleValueType( op.FirstResult.Type );

            if( op.FirstResult.Type == m_wkt.System_IntPtr ||
                op.FirstResult.Type == m_wkt.System_UIntPtr ||
                op.FirstResult.Type is TS.PointerTypeRepresentation )
            {
                resultType = m_manager.GetOrInsertBasicTypeAsLLVMSingleValueType( m_wkt.System_UInt32 );
            }

            if( op is IR.ZeroExtendOperator )
            {
                v = m_basicBlock.InsertZExt( v, resultType, 8 * ( int )op.SignificantSize );
            }
            else if( op is IR.SignExtendOperator )
            {
                v = m_basicBlock.InsertSExt( v, resultType, 8 * ( int )op.SignificantSize );
            }
            else if( op is IR.TruncateOperator )
            {
                v = m_basicBlock.InsertTrunc( v, resultType, 8 * ( int )op.SignificantSize );
            }
            else
            {
                throw new Exception( "Unimplemented Conversion Operator: " + op.ToString( ) );
            }

            StoreValue( m_results[ 0 ], v );
        }

        private void Translate_ConvertOperator( IR.ConvertOperator op )
        {
            // TODO: Add support for overflow exceptions
            _Value v = ConvertValueToALUOperableType( m_arguments[ 0 ] );
            StoreValue( m_results[ 0 ], v );
        }

        private void Translate_SingleAssignmentOperator( IR.SingleAssignmentOperator op )
        {
            StoreValue( m_results[ 0 ], m_arguments[ 0 ] );
        }

        private void Translate_AddressAssignmentOperator( IR.AddressAssignmentOperator op )
        {
            StoreValue( m_results[ 0 ], m_basicBlock.GetAddressAsUIntPtr( m_arguments[ 0 ] ) );
        }

        private void Translate_InitialValueOperator( IR.InitialValueOperator op )
        {
            if (!(op.FirstResult is IR.ArgumentVariableExpression))
            {
                throw new Exception("InitialValueOperator implemented only for argument variables: " + op.ToString());
            }

            int index = op.FirstResult.Number;
            if (m_method is TS.StaticMethodRepresentation)
            {
                --index;
            }

            m_basicBlock.InsertStoreArgument(m_results[0], index);
        }

        _Value DoCmpOp( _Value valA, _Value valB, int cond, bool signed )
        {
            valA = ConvertValueToALUOperableType( valA );
            valB = ConvertValueToALUOperableType( valB );
            return m_basicBlock.InsertCmp( cond, signed, valA, valB );
        }

        private void Translate_CompareAndSetOperator( IR.CompareAndSetOperator op )
        {
            _Value v = DoCmpOp( m_arguments[ 0 ], m_arguments[ 1 ], ( int )op.Condition, op.Signed );
            StoreValue( m_results[ 0 ], v );
        }

        private void Translate_LoadIndirectOperator( IR.LoadIndirectOperator op )
        {
            StoreValue( m_results[ 0 ], m_basicBlock.LoadIndirect( m_arguments[ 0 ], m_manager.GetOrInsertType( op.Type ) ) );
        }

        private void Translate_StoreIndirectOperator( IR.StoreIndirectOperator op )
        {
            StoreValue( m_basicBlock.LoadIndirect( m_arguments[ 0 ], m_manager.GetOrInsertType( op.Type ) ), m_arguments[ 1 ] );
        }

        private void Translate_StoreInstanceFieldOperator( IR.StoreInstanceFieldOperator op )
        {
            _Value value = m_basicBlock.GetField( m_arguments[ 0 ], op.Field.Offset );
            StoreValue( value, m_arguments[ 1 ] );
        }

        private void Translate_LoadInstanceFieldOperator( IR.LoadInstanceFieldOperator op )
        {
            _Value value = m_basicBlock.GetField( m_arguments[ 0 ], op.Field.Offset );
            StoreValue( m_results[ 0 ], value );
        }

        private void Translate_LoadInstanceFieldAddressOperator( IR.LoadInstanceFieldAddressOperator op )
        {
            _Value value = m_basicBlock.GetField( m_arguments[ 0 ], op.Field.Offset );
            StoreValue( m_results[ 0 ], m_basicBlock.GetAddressAsUIntPtr( value ) );
        }

        private _Value ArrayAccessByIDX( _Value array, _Value idx )
        {
            array = m_basicBlock.GetField( array, ( int )m_wkt.System_Array.Size );
            return m_basicBlock.IndexLLVMArray( array, ConvertValueToALUOperableType( idx ) );
        }

        private void Translate_LoadElementOperator( IR.LoadElementOperator op )
        {
            _Value v = ArrayAccessByIDX( m_arguments[ 0 ], m_arguments[ 1 ] );
            StoreValue( m_results[ 0 ], v );
        }

        private void Translate_LoadElementAddressOperator( IR.LoadElementAddressOperator op )
        {
            _Value v = ArrayAccessByIDX( m_arguments[ 0 ], m_arguments[ 1 ] );
            StoreValue( m_results[ 0 ], m_basicBlock.GetAddressAsUIntPtr( v ) );
        }

        private void Translate_StoreElementOperator( IR.StoreElementOperator op )
        {
            _Value v = ArrayAccessByIDX( m_arguments[ 0 ], m_arguments[ 1 ] );
            StoreValue( v, m_arguments[ 2 ] ); 
        }

        private void Translate_UnconditionalControlOperator( IR.UnconditionalControlOperator op )
        {
            m_basicBlock.InsertUnconditionalBranch( GetOrInsertBasicBlock( op.TargetBranch ) );
        }

        private void Translate_BinaryConditionalControlOperator( IR.BinaryConditionalControlOperator op )
        {
            m_basicBlock.InsertConditionalBranch(
                ConvertValueToALUOperableType( m_arguments[ 0 ] ),
                GetOrInsertBasicBlock( op.TargetBranchTaken ),
                GetOrInsertBasicBlock( op.TargetBranchNotTaken ) );
        }

        private void Translate_CompareConditionalControlOperator( IR.CompareConditionalControlOperator op )
        {
            _Value v = DoCmpOp( m_arguments[ 0 ], m_arguments[ 1 ], ( int )op.Condition, op.Signed );
            m_basicBlock.InsertConditionalBranch( v,
                GetOrInsertBasicBlock( op.TargetBranchTaken ),
                GetOrInsertBasicBlock( op.TargetBranchNotTaken ) );
        }

        private void Translate_MultiWayConditionalControlOperator( IR.MultiWayConditionalControlOperator op )
        {
            List<int> caseValues = new List<int>( );
            List<LLVM._BasicBlock> caseBBs = new List<LLVM._BasicBlock>( );

            for( int i = 0; i < op.Targets.Length; ++i )
            {
                caseValues.Add( i );
                caseBBs.Add( GetOrInsertBasicBlock( op.Targets[ i ] ) );
            }

            _Value argument = ConvertValueToALUOperableType( m_arguments[ 0 ] );
            _BasicBlock defaultBlock = GetOrInsertBasicBlock( op.TargetBranchNotTaken );
            m_basicBlock.InsertSwitchAndCases( argument, defaultBlock, caseValues, caseBBs );
        }

        private void Translate_ReturnControlOperator( IR.ReturnControlOperator op )
        {
            switch( op.Arguments.Length )
            {
                case 0:
                    m_basicBlock.InsertRet( null );
                    break;
                case 1:
                    m_basicBlock.InsertRet( m_arguments[ 0 ] );
                    break;
                default:
                    throw new System.InvalidOperationException( "ReturnControlOperator with more than one arg not supported. " + op );
            }
        }

        private void Translate_LeaveControlOperator( IR.LeaveControlOperator op )
        {
            //Jump to a finally block.           
            m_basicBlock.InsertUnconditionalBranch( GetOrInsertBasicBlock( op.TargetBranch ) );
        }
        private void Translate_DeadControlOperator( IR.DeadControlOperator op )
        {
            //Basic Block marked as dead code by Zelig.
            //To keep LLVM able to compile, insert a branch to itself.
            m_basicBlock.InsertUnconditionalBranch( m_basicBlock );
        }

        private bool ReplaceMethodCallWithIntrinsic( IR.CallOperator op, List<_Value> convertedArgs )
        {
            TS.WellKnownMethods wkm = m_cfg.TypeSystem.WellKnownMethods;

            // System.Buffer.InternalMemoryCopy(byte*, byte*, int) => llvm.memcpy
            // System.Buffer.InternalBackwardMemoryCopy(byte*, byte*, int) => llvm.memmove
            if( ( op.TargetMethod == wkm.System_Buffer_InternalMemoryCopy ) ||
                ( op.TargetMethod == wkm.System_Buffer_InternalBackwardMemoryCopy ) )
            {
                bool overlapping = op.TargetMethod != wkm.System_Buffer_InternalMemoryCopy;

                _Value src = convertedArgs[ 0 ];
                _Value dst = convertedArgs[ 1 ];
                _Value size = convertedArgs[ 2 ];
                m_basicBlock.InsertMemCpy( dst, src, size, overlapping );
                return true;
            }

            // Microsoft.Zelig.Runtime.Memory.Fill(byte*, int, byte) => llvm.memset
            if( op.TargetMethod == wkm.Microsoft_Zelig_Runtime_Memory_Fill )
            {
                _Value dst = convertedArgs[ 0 ];
                _Value size = convertedArgs[ 1 ];
                _Value value = convertedArgs[ 2 ];
                m_basicBlock.InsertMemSet( dst, value, size );
                return true;
            }

            return false;
        }

        private void BuildMethodCallInstructions( IR.CallOperator op, bool callIndirect = false )
        {
            List<_Value> args = new List<_Value>( );

            int i = 0;

            if( op.TargetMethod is TS.StaticMethodRepresentation )
                ++i;
            if( callIndirect )
                ++i;

            for( ; i < op.Arguments.Length; ++i )
            {
                var opArgTypeRep = op.TargetMethod.ThisPlusArguments[ i - ( callIndirect ? 1 : 0 ) ];
                var opArgType = m_manager.GetOrInsertType( opArgTypeRep );
                var convertedArg = ConvertValueToStoreToTarget( m_arguments[ i ], opArgType );
                args.Add( convertedArg );
            }

            if( ReplaceMethodCallWithIntrinsic( op, args ) )
            {
                return;
            }

            LLVM._Function targetFunc = m_manager.GetOrInsertFunction( op.TargetMethod );

            if( op.TargetMethod.Flags.HasFlag( TS.MethodRepresentation.Attributes.PinvokeImpl ) )
            {
                targetFunc.SetExternalLinkage( );
            }

            _Value ret;

            if( !callIndirect )
            {
                ret = m_basicBlock.InsertCall( targetFunc, args );
            }
            else
            {
                ret = m_basicBlock.InsertIndirectCall( targetFunc, m_arguments[ 0 ], args );
            }

            if( op.Results.Length == 1 )
            {
                StoreValue( m_results[ 0 ], ret );
            }
            else if( op.Results.Length > 1 )
            {
                throw new System.InvalidOperationException( "More than one return values are not handled." );
            }
        }

        private void Translate_StaticCallOperator( IR.StaticCallOperator op )
        {
            BuildMethodCallInstructions( op );
        }
        private void Translate_InstanceCallOperator( IR.InstanceCallOperator op )
        {
            BuildMethodCallInstructions( op );
        }
        private void Translate_IndirectCallOperator( IR.IndirectCallOperator op )
        {
            BuildMethodCallInstructions( op, true );
        }

        private void Translate_NopOperator( IR.NopOperator op )
        {
            //No Operation.
            //TODO: Add no-op equivalent for debug builds to avoid losing debug metadata.
        }
    }
}
