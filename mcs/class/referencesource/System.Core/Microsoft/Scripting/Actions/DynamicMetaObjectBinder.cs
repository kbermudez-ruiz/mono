﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Apache License, Version 2.0, please send an email to 
 * dlr@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

#if CLR2
using Microsoft.Scripting.Ast;
using Microsoft.Scripting.Ast.Compiler;
#else
using System.Linq.Expressions;
using System.Linq.Expressions.Compiler;
#endif

#if SILVERLIGHT
using System.Core;
#else
using System.Runtime.Remoting;
#endif

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Dynamic.Utils;
using System.Runtime.CompilerServices;

namespace System.Dynamic {
    /// <summary>
    /// The dynamic call site binder that participates in the <see cref="DynamicMetaObject"/> binding protocol.
    /// </summary>
    /// <remarks>
    /// The <see cref="CallSiteBinder"/> performs the binding of the dynamic operation using the runtime values
    /// as input. On the other hand, the <see cref="DynamicMetaObjectBinder"/> participates in the <see cref="DynamicMetaObject"/>
    /// binding protocol.
    /// </remarks>
    public abstract class DynamicMetaObjectBinder : CallSiteBinder {

        #region Public APIs

        /// <summary>
        /// Initializes a new instance of the <see cref="DynamicMetaObjectBinder"/> class.
        /// </summary>
        protected DynamicMetaObjectBinder() {
        }

        /// <summary>
        /// The result type of the operation.
        /// </summary>
        public virtual Type ReturnType {
            get { return typeof(object); }
        }

        /// <summary>
        /// Performs the runtime binding of the dynamic operation on a set of arguments.
        /// </summary>
        /// <param name="args">An array of arguments to the dynamic operation.</param>
        /// <param name="parameters">The array of <see cref="ParameterExpression"/> instances that represent the parameters of the call site in the binding process.</param>
        /// <param name="returnLabel">A LabelTarget used to return the result of the dynamic binding.</param>
        /// <returns>
        /// An Expression that performs tests on the dynamic operation arguments, and
        /// performs the dynamic operation if the tests are valid. If the tests fail on
        /// subsequent occurrences of the dynamic operation, Bind will be called again
        /// to produce a new <see cref="Expression"/> for the new argument types.
        /// </returns>
        public sealed override Expression Bind(object[] args, ReadOnlyCollection<ParameterExpression> parameters, LabelTarget returnLabel) {
            ContractUtils.RequiresNotNull(args, "args");
            ContractUtils.RequiresNotNull(parameters, "parameters");
            ContractUtils.RequiresNotNull(returnLabel, "returnLabel");
            if (args.Length == 0) {
                throw Error.OutOfRange("args.Length", 1);
            }
            if (parameters.Count == 0) {
                throw Error.OutOfRange("parameters.Count", 1);
            }
            if (args.Length != parameters.Count) {
                throw new ArgumentOutOfRangeException("args");
            }

            // Ensure that the binder's ReturnType matches CallSite's return
            // type. We do this so meta objects and language binders can
            // compose trees together without needing to insert converts.
            Type expectedResult;
            if (IsStandardBinder) {
                expectedResult = ReturnType;

                if (returnLabel.Type != typeof(void) &&
                    !TypeUtils.AreReferenceAssignable(returnLabel.Type, expectedResult)) {
                    throw Error.BinderNotCompatibleWithCallSite(expectedResult, this, returnLabel.Type);
                }
            } else {
                // Even for non-standard binders, we have to at least make sure
                // it works with the CallSite's type to build the return.
                expectedResult = returnLabel.Type;
            }

            DynamicMetaObject target = DynamicMetaObject.Create(args[0], parameters[0]);
            DynamicMetaObject[] metaArgs = CreateArgumentMetaObjects(args, parameters);

            DynamicMetaObject binding = Bind(target, metaArgs);

            if (binding == null) {
                throw Error.BindingCannotBeNull();
            }

            Expression body = binding.Expression;
            BindingRestrictions restrictions = binding.Restrictions;

            // Ensure the result matches the expected result type.
            if (expectedResult != typeof(void) &&
                !TypeUtils.AreReferenceAssignable(expectedResult, body.Type)) {

                //
                // Blame the last person that handled the result: assume it's
                // the dynamic object (if any), otherwise blame the language.
                //
                if (target.Value is IDynamicMetaObjectProvider) {
                    throw Error.DynamicObjectResultNotAssignable(body.Type, target.Value.GetType(), this, expectedResult);
                } else {
                    throw Error.DynamicBinderResultNotAssignable(body.Type, this, expectedResult);
                }
            }

            // if the target is IDO, standard binders ask it to bind the rule so we may have a target-specific binding. 
            // it makes sense to restrict on the target's type in such cases.
            // ideally IDO metaobjects should do this, but they often miss that type of "this" is significant.
            if (IsStandardBinder && args[0] as IDynamicMetaObjectProvider != null) {
                if (restrictions == BindingRestrictions.Empty) {
                    throw Error.DynamicBindingNeedsRestrictions(target.Value.GetType(), this);
                }
            }

            restrictions = AddRemoteObjectRestrictions(restrictions, args, parameters);

            // Add the return
            if (body.NodeType != ExpressionType.Goto) {
                body = Expression.Return(returnLabel, body);
            }

            // Finally, add restrictions
            if (restrictions != BindingRestrictions.Empty) {
                body = Expression.IfThen(restrictions.ToExpression(), body);
            }

            return body;
        }

        private static DynamicMetaObject[] CreateArgumentMetaObjects(object[] args, ReadOnlyCollection<ParameterExpression> parameters) {
            DynamicMetaObject[] mos;
            if (args.Length != 1) {
                mos = new DynamicMetaObject[args.Length - 1];
                for (int i = 1; i < args.Length; i++) {
                    mos[i - 1] = DynamicMetaObject.Create(args[i], parameters[i]);
                }
            } else {
                mos = DynamicMetaObject.EmptyMetaObjects;
            }
            return mos;
        }

        private static BindingRestrictions AddRemoteObjectRestrictions(BindingRestrictions restrictions, object[] args, ReadOnlyCollection<ParameterExpression> parameters) {
#if !SILVERLIGHT

            for (int i = 0; i < parameters.Count; i++) {
                var expr = parameters[i];
                var value = args[i] as MarshalByRefObject;

                // special case for MBR objects.
                // when MBR objects are remoted they can have different conversion behavior
                // so bindings created for local and remote objects should not be mixed.
                if (value != null && !IsComObject(value)) {
                    BindingRestrictions remotedRestriction;
                    if (RemotingServices.IsObjectOutOfAppDomain(value)) {
                        remotedRestriction = BindingRestrictions.GetExpressionRestriction(
                            Expression.AndAlso(
                                Expression.NotEqual(expr, Expression.Constant(null)),
                                Expression.Call(
                                    typeof(RemotingServices).GetMethod("IsObjectOutOfAppDomain"),
                                    expr
                                )
                            )
                        );
                    } else {
                        remotedRestriction = BindingRestrictions.GetExpressionRestriction(
                            Expression.AndAlso(
                                Expression.NotEqual(expr, Expression.Constant(null)),
                                Expression.Not(
                                    Expression.Call(
                                        typeof(RemotingServices).GetMethod("IsObjectOutOfAppDomain"),
                                        expr
                                    )
                                )
                            )
                        );
                    }
                    restrictions = restrictions.Merge(remotedRestriction);
                }
            }

#endif
            return restrictions;
        }

        /// <summary>
        /// When overridden in the derived class, performs the binding of the dynamic operation.
        /// </summary>
        /// <param name="target">The target of the dynamic operation.</param>
        /// <param name="args">An array of arguments of the dynamic operation.</param>
        /// <returns>The <see cref="DynamicMetaObject"/> representing the result of the binding.</returns>
        public abstract DynamicMetaObject Bind(DynamicMetaObject target, DynamicMetaObject[] args);

        /// <summary>
        /// Gets an expression that will cause the binding to be updated. It
        /// indicates that the expression's binding is no longer valid.
        /// This is typically used when the "version" of a dynamic object has
        /// changed.
        /// </summary>
        /// <param name="type">The <see cref="Expression.Type">Type</see> property of the resulting expression; any type is allowed.</param>
        /// <returns>The update expression.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
        public Expression GetUpdateExpression(Type type) {
            return Expression.Goto(CallSiteBinder.UpdateLabel, type);
        }

        /// <summary>
        /// Defers the binding of the operation until later time when the runtime values of all dynamic operation arguments have been computed.
        /// </summary>
        /// <param name="target">The target of the dynamic operation.</param>
        /// <param name="args">An array of arguments of the dynamic operation.</param>
        /// <returns>The <see cref="DynamicMetaObject"/> representing the result of the binding.</returns>
        public DynamicMetaObject Defer(DynamicMetaObject target, params DynamicMetaObject[] args) {
            ContractUtils.RequiresNotNull(target, "target");

            if (args == null) {
                return MakeDeferred(target.Restrictions, target);
            } else {
                return MakeDeferred(
                    target.Restrictions.Merge(BindingRestrictions.Combine(args)),
                    args.AddFirst(target)
                );
            }
        }

        /// <summary>
        /// Defers the binding of the operation until later time when the runtime values of all dynamic operation arguments have been computed.
        /// </summary>
        /// <param name="args">An array of arguments of the dynamic operation.</param>
        /// <returns>The <see cref="DynamicMetaObject"/> representing the result of the binding.</returns>
        public DynamicMetaObject Defer(params DynamicMetaObject[] args) {
            return MakeDeferred(BindingRestrictions.Combine(args), args);
        }

        private DynamicMetaObject MakeDeferred(BindingRestrictions rs, params DynamicMetaObject[] args) {
            var exprs = DynamicMetaObject.GetExpressions(args);

            Type delegateType = DelegateHelpers.MakeDeferredSiteDelegate(args, ReturnType);

            // Because we know the arguments match the delegate type (we just created the argument types)
            // we go directly to DynamicExpression.Make to avoid a bunch of unnecessary argument validation
            return new DynamicMetaObject(
                DynamicExpression.Make(ReturnType, delegateType, this, new TrueReadOnlyCollection<Expression>(exprs)),
                rs
            );
        }

        #endregion

        // used to detect standard MetaObjectBinders.
        internal virtual bool IsStandardBinder {
            get {
                return false;
            }
        }

#if !SILVERLIGHT
        private static readonly Type ComObjectType = typeof(object).Assembly.GetType("System.__ComObject");
        private static bool IsComObject(object obj) {
            // we can't use System.Runtime.InteropServices.Marshal.IsComObject(obj) since it doesn't work in partial trust
            return obj != null && ComObjectType.IsAssignableFrom(obj.GetType());
        }
#endif

    }
}
