﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AsyncRewriter.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AsyncRewriter
{
    /// <summary>
    /// </summary>
    /// <remarks>
    /// http://stackoverflow.com/questions/2961753/how-to-hide-files-generated-by-custom-tool-in-visual-studio
    /// </remarks>
    public class Rewriter
    {
        /// <summary>
        /// Invocations of methods on these types never get rewritten to async
        /// </summary>
        HashSet<ITypeSymbol> _excludedTypes;

        /// <summary>
        /// Using directives required for async, not expected to be in the source (sync) files
        /// </summary>
        static readonly UsingDirectiveSyntax[] ExtraUsingDirectives = {
            SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Threading")),
            SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Threading.Tasks")),
        };

        /// <summary>
        /// Calls of methods on these types never get rewritten, because they aren't actually
        /// asynchronous. An additional user-determined list may also be passed in.
        /// </summary>
        static readonly string[] AlwaysExcludedTypes = {
            "System.IO.TextWriter",
            "System.IO.StringWriter",
            "System.IO.MemoryStream"
        };

        /// <summary>
        /// Contains the parsed contents of the AsyncRewriterHelpers.cs file (essentially
        /// <see cref="RewriteAsync"/> which needs to always be compiled in.
        /// </summary>
        //readonly SyntaxTree _asyncHelpersSyntaxTree;

        ITypeSymbol _cancellationTokenSymbol;

        readonly ILogger _log;

        public Rewriter(ILogger log=null)
        {
            _log = log ?? new ConsoleLoggingAdapter();
            // ReSharper disable once AssignNullToNotNullAttribute
            /*using (var reader = new StreamReader(typeof(Rewriter).GetTypeInfo().Assembly.GetManifestResourceStream("AsyncRewriter.AsyncRewriterHelpers.cs")))
            {
                _asyncHelpersSyntaxTree = SyntaxFactory.ParseSyntaxTree(reader.ReadToEnd());
            }*/
        }

        public string RewriteAndMerge(string[] paths, string[] additionalAssemblyNames=null, string[] excludedTypes = null)
        {
            //if (paths.All(p => Path.GetFileName(p) != "AsyncRewriterHelpers.cs"))
                //throw new ArgumentException("AsyncRewriterHelpers.cs must be included in paths", nameof(paths));
            Contract.EndContractBlock();

            var syntaxTrees = paths.Select(p => SyntaxFactory.ParseSyntaxTree(File.ReadAllText(p))).ToArray();

            var compilation = CSharpCompilation.Create("Temp", syntaxTrees, null, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddReferences(
                        MetadataReference.CreateFromFile(typeof(object).GetTypeInfo().Assembly.Location),
                        MetadataReference.CreateFromFile(typeof(Stream).GetTypeInfo().Assembly.Location)
                );
            if (additionalAssemblyNames != null)
            {
				var assemblyPath = Path.GetDirectoryName(typeof(object).GetTypeInfo().Assembly.Location);

				compilation = compilation.AddReferences(additionalAssemblyNames.Select(n =>
				{
					if (File.Exists(n))
					{
						return MetadataReference.CreateFromFile(n);
					}
					else if (File.Exists(Path.Combine(assemblyPath, n)))
					{
						return MetadataReference.CreateFromFile(Path.Combine(assemblyPath, n));
					}
					else
					{
						return null;
					}
				}).Where(c => c != null));
            }

            return RewriteAndMerge(syntaxTrees, compilation, excludedTypes).ToString();
        }

	    private class UsingsComparer
		    : IEqualityComparer<UsingDirectiveSyntax>
	    {
		    public static readonly UsingsComparer Default = new UsingsComparer();

		    private UsingsComparer()
		    {
		    }

			public bool Equals(UsingDirectiveSyntax x, UsingDirectiveSyntax y)
			{
				return x.Name.ToString() == y.Name.ToString();
			}

		    public int GetHashCode(UsingDirectiveSyntax obj)
		    {
			    return obj.Name.ToString().GetHashCode();
		    }
	    }

        public SyntaxTree RewriteAndMerge(SyntaxTree[] syntaxTrees, CSharpCompilation compilation, string[] excludedTypes = null)
        {
            var rewrittenTrees = Rewrite(syntaxTrees, compilation, excludedTypes).ToArray();

            return SyntaxFactory.SyntaxTree(
                SyntaxFactory.CompilationUnit()
                    .WithUsings(SyntaxFactory.List(
                        new HashSet<UsingDirectiveSyntax>(rewrittenTrees.SelectMany(t => t.GetCompilationUnitRoot().Usings), UsingsComparer.Default)
                    ))
                    .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(
                        rewrittenTrees
                            .SelectMany(t => t.GetCompilationUnitRoot().Members)
                            .Cast<NamespaceDeclarationSyntax>()
                            .SelectMany(ns => ns.Members)
                            .Cast<ClassDeclarationSyntax>()
                            .GroupBy(cls => cls.FirstAncestorOrSelf<NamespaceDeclarationSyntax>().Name.ToString())
                            .Select(g => SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(g.Key))
                                .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(g))
                            )
                    ))
                    .WithEndOfFileToken(SyntaxFactory.Token(SyntaxKind.EndOfFileToken))
                    .NormalizeWhitespace()
            );
        }

        public IEnumerable<SyntaxTree> Rewrite(SyntaxTree[] syntaxTrees, CSharpCompilation compilation, string[] excludedTypes=null)
        {
            _cancellationTokenSymbol = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");

            _excludedTypes = new HashSet<ITypeSymbol>();

            // Handle the user-provided exclude list
            if (excludedTypes != null)
            {
                var excludedTypeSymbols = excludedTypes.Select(compilation.GetTypeByMetadataName).ToList();
                var notFound = excludedTypeSymbols.IndexOf(null);
                if (notFound != -1)
                    throw new ArgumentException($"Type {excludedTypes[notFound]} not found in compilation", nameof(excludedTypes));
                _excludedTypes.UnionWith(excludedTypeSymbols);
            }

            // And the builtin exclude list
            _excludedTypes.UnionWith(
                AlwaysExcludedTypes
                    .Select(compilation.GetTypeByMetadataName)
                    .Where(sym => sym != null)
            );

            foreach (var syntaxTree in syntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree, true);
                if (semanticModel == null)
                    throw new ArgumentException("A provided syntax tree was compiled into the provided compilation");

                var usings = syntaxTree.GetCompilationUnitRoot().Usings;

				if (!syntaxTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Any(m => m.AttributeLists.SelectMany(al => al.Attributes).Any(a => a.Name.ToString().Contains("RewriteAsync"))))
                {
                    continue;
                }

                usings = usings.AddRange(ExtraUsingDirectives);

                // Add #pragma warning disable at the top of the file
                usings = usings.Replace(usings[0], usings[0].WithLeadingTrivia(SyntaxFactory.Trivia(SyntaxFactory.PragmaWarningDirectiveTrivia(SyntaxFactory.Token(SyntaxKind.DisableKeyword), true))));
                    
                var namespaces = SyntaxFactory.List<MemberDeclarationSyntax>(
                    syntaxTree.GetRoot()
                    .DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .Where(m => m.AttributeLists.SelectMany(al => al.Attributes).Any(a => a.Name.ToString().Contains("RewriteAsync")))
                    .GroupBy(m => m.FirstAncestorOrSelf<ClassDeclarationSyntax>())
                    .GroupBy(g => g.Key.FirstAncestorOrSelf<NamespaceDeclarationSyntax>())
                    .Select(nsGrp =>
                        SyntaxFactory.NamespaceDeclaration(nsGrp.Key.Name)
                        .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(nsGrp.Select(clsGrp =>
                            SyntaxFactory.ClassDeclaration(clsGrp.Key.Identifier)
                                .WithModifiers(clsGrp.Key.Modifiers)
                                .WithTypeParameterList(clsGrp.Key.TypeParameterList)
                                .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(
                                    clsGrp.SelectMany(m => RewriteMethods(m, semanticModel))
                                ))
                        )))
                    )
                );

                yield return SyntaxFactory.SyntaxTree(
                    SyntaxFactory.CompilationUnit()
                        .WithUsings(SyntaxFactory.List(usings))
                        .WithMembers(namespaces)
                        .WithEndOfFileToken(SyntaxFactory.Token(SyntaxKind.EndOfFileToken))
                        .NormalizeWhitespace()
                );
            }
        }

	    IEnumerable<MethodDeclarationSyntax> RewriteMethods(MethodDeclarationSyntax inMethodSyntax, SemanticModel semanticModel)
	    {
		    yield return RewriteMethodAsync(inMethodSyntax, semanticModel);
		    yield return RewriteMethodAsyncWithCancellationToken(inMethodSyntax, semanticModel);
	    }

	    MethodDeclarationSyntax RewriteMethodAsync(MethodDeclarationSyntax inMethodSyntax, SemanticModel semanticModel)
	    {
			var inMethodSymbol = semanticModel.GetDeclaredSymbol(inMethodSyntax);

			//Log.LogMessage("Method {0}: {1}", inMethodInfo.Symbol.Name, inMethodInfo.Symbol.);

			var outMethodName = inMethodSyntax.Identifier.Text + "Async";

			_log.Debug("  Rewriting method {0} to {1}", inMethodSymbol.Name, outMethodName);

            var cancellation = SyntaxFactory.Argument(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("CancellationToken"), SyntaxFactory.IdentifierName("None")));

	        var parameters = inMethodSymbol.Parameters.Select(c => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(c.Name))).ToList();

	        parameters.Insert(inMethodSymbol.Parameters.TakeWhile(p => !p.HasExplicitDefaultValue && !p.IsParams).Count(), cancellation);

            var methodInvocation = SyntaxFactory.InvocationExpression
            (
                SyntaxFactory.IdentifierName(outMethodName),
                SyntaxFactory.ArgumentList(new SeparatedSyntaxList<ArgumentSyntax>().AddRange(parameters))
            );

            var callAsyncWithCancellationToken = methodInvocation;
			
            var outMethod = inMethodSyntax.WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(callAsyncWithCancellationToken)));

			// Method signature
		    outMethod = outMethod
			    .WithIdentifier(SyntaxFactory.Identifier(outMethodName))
			    .WithAttributeLists(new SyntaxList<AttributeListSyntax>());

			// Transform return type adding Task<>
			var returnType = inMethodSyntax.ReturnType.ToString();
				outMethod = outMethod.WithReturnType(SyntaxFactory.ParseTypeName(
				returnType == "void" ? "Task" : $"Task<{returnType}>")
			);

			var parentContainsAsyncMethod = GetAllMembers(inMethodSymbol.ReceiverType.BaseType).Any(c => c.Name == outMethodName);
			var parentContainsMethodWithRewriteAsync = GetAllMembers(inMethodSymbol.ReceiverType.BaseType)
				.Where(c => c.Name == inMethodSyntax.Identifier.Text)
				.Any(m => m.GetAttributes().Any(a => a.AttributeClass.Name.Contains("RewriteAsync")));
            
             // Remove the override and new attributes. Seems like the clean .Remove above doesn't work...
            if (!(parentContainsAsyncMethod || parentContainsMethodWithRewriteAsync))
			{
			    for (var i = 0; i < outMethod.Modifiers.Count;)
			    {
				    var text = outMethod.Modifiers[i].Text;
				    if (text == "override" || text == "new")
				    {
					    outMethod = outMethod.WithModifiers(outMethod.Modifiers.RemoveAt(i));
					    continue;
				    }
				    i++;
			    }
		    }

            var attr = inMethodSymbol.GetAttributes().Single(a => a.AttributeClass.Name.EndsWith("RewriteAsyncAttribute"));

            if (attr.ConstructorArguments.Length > 0 && (bool)attr.ConstructorArguments[0].Value)
            {
                for (var i = 0; i < outMethod.Modifiers.Count;)
                {
                    var text = outMethod.Modifiers[i].Text;

                    if (text == "public" || text == "private" || text == "protected")
                    {
                        outMethod = outMethod.WithModifiers(outMethod.Modifiers.RemoveAt(i));
                        continue;
                    }

                    i++;
                }

                outMethod = outMethod.WithModifiers(outMethod.Modifiers.Insert(0, SyntaxFactory.Token(SyntaxKind.PublicKeyword)));
            }

            return outMethod;
		}

	    private IEnumerable<ISymbol> GetAllMembers(ITypeSymbol symbol)
	    {
		    foreach (var member in symbol.GetMembers())
		    {
				yield return member;
		    }

		    if (symbol.BaseType != null)
		    {
			    foreach (var member in symbol.BaseType.GetMembers())
			    {
				    yield return member;
			    }
		    }
	    }

		MethodDeclarationSyntax RewriteMethodAsyncWithCancellationToken(MethodDeclarationSyntax inMethodSyntax, SemanticModel semanticModel)
		{
            var inMethodSymbol = semanticModel.GetDeclaredSymbol(inMethodSyntax);

            //Log.LogMessage("Method {0}: {1}", inMethodInfo.Symbol.Name, inMethodInfo.Symbol.);

            var outMethodName = inMethodSyntax.Identifier.Text + "Async";

            _log.Info("  Rewriting method {0} to {1}", inMethodSymbol.Name, outMethodName);

            // Visit all method invocations inside the method, rewrite them to async if needed
            var rewriter = new MethodInvocationRewriter(_log, semanticModel, _excludedTypes, _cancellationTokenSymbol);
            var outMethod = (MethodDeclarationSyntax)rewriter.Visit(inMethodSyntax);

            // Method signature
            outMethod = outMethod
                .WithIdentifier(SyntaxFactory.Identifier(outMethodName))
                .WithAttributeLists(new SyntaxList<AttributeListSyntax>())
                .WithModifiers(inMethodSyntax.Modifiers
                  .Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
                  //.Remove(SyntaxFactory.Token(SyntaxKind.OverrideKeyword))
                  //.Remove(SyntaxFactory.Token(SyntaxKind.NewKeyword))
                )
                // Insert the cancellation token into the parameter list at the right place
                .WithParameterList(SyntaxFactory.ParameterList(inMethodSyntax.ParameterList.Parameters.Insert(
                    inMethodSyntax.ParameterList.Parameters.TakeWhile(p => p.Default == null && !p.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword))).Count(),
                    SyntaxFactory.Parameter(
                            SyntaxFactory.List<AttributeListSyntax>(),
                            SyntaxFactory.TokenList(),
                            SyntaxFactory.ParseTypeName("CancellationToken"),
                            SyntaxFactory.Identifier("cancellationToken"),
                            null
                ))));

            // Transform return type adding Task<>
            var returnType = inMethodSyntax.ReturnType.ToString();
            outMethod = outMethod.WithReturnType(SyntaxFactory.ParseTypeName(
                returnType == "void" ? "Task" : $"Task<{returnType}>")
            );

			var parentContainsAsyncMethod = GetAllMembers(inMethodSymbol.ReceiverType.BaseType).Any(c => c.Name == outMethodName);
			var parentContainsMethodWithRewriteAsync = GetAllMembers(inMethodSymbol.ReceiverType.BaseType)
				.Where(c => c.Name == inMethodSyntax.Identifier.Text)
				.Any(m => m.GetAttributes().Any(a => a.AttributeClass.Name.Contains("RewriteAsync")));
			
			// Remove the override and new attributes. Seems like the clean .Remove above doesn't work...
			if (!(parentContainsAsyncMethod || parentContainsMethodWithRewriteAsync))
			{
				for (var i = 0; i < outMethod.Modifiers.Count;)
				{
					var text = outMethod.Modifiers[i].Text;
					if (text == "override" || text == "new")
					{
						outMethod = outMethod.WithModifiers(outMethod.Modifiers.RemoveAt(i));
						continue;
					}
					i++;
				}
			}

            var attr = inMethodSymbol.GetAttributes().Single(a => a.AttributeClass.Name.EndsWith("RewriteAsyncAttribute"));

            if (attr.ConstructorArguments.Length > 0 && (bool)attr.ConstructorArguments[0].Value)
            {
                for (var i = 0; i < outMethod.Modifiers.Count;)
                {
                    var text = outMethod.Modifiers[i].Text;

                    if (text == "public" || text == "private" || text == "protected")
                    {
                        outMethod = outMethod.WithModifiers(outMethod.Modifiers.RemoveAt(i));
                        continue;
                    }

                    i++;
                }

                outMethod = outMethod.WithModifiers(outMethod.Modifiers.Insert(0, SyntaxFactory.Token(SyntaxKind.PublicKeyword)));
            }

            return outMethod;
        }
    }

    internal class MethodInvocationRewriter : CSharpSyntaxRewriter
    {
        readonly SemanticModel _model;
        readonly HashSet<ITypeSymbol> _excludeTypes;
        readonly ITypeSymbol _cancellationTokenSymbol;
        readonly ParameterComparer _paramComparer;
        readonly ILogger _log;

        public MethodInvocationRewriter(ILogger log, SemanticModel model, HashSet<ITypeSymbol> excludeTypes,
                                        ITypeSymbol cancellationTokenSymbol)
        {
            _log = log;
            _model = model;
            _cancellationTokenSymbol = cancellationTokenSymbol;
            _excludeTypes = excludeTypes;
            _paramComparer = new ParameterComparer();
        }

        public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var syncSymbol = (IMethodSymbol)_model.GetSymbolInfo(node).Symbol;

	       if (syncSymbol == null)
	        {
		        return node;
	        }

	        var cancellationTokenPos = -1;

	        // Skip invocations of methods that don't have [RewriteAsync], or an Async
            // counterpart to them
            if (syncSymbol.GetAttributes().Any(a => a.AttributeClass.Name.Contains("RewriteAsync")))
            {
                // This is one of our methods, flagged for async rewriting.
                // Find the proper position for the cancellation token
                cancellationTokenPos = syncSymbol.Parameters.TakeWhile(p => !p.IsOptional && !p.IsParams).Count();
            }
            else
            {
                if (_excludeTypes.Contains(syncSymbol.ContainingType))
                    return node;
                
				var asyncCandidates = syncSymbol
					.ContainingType
					.GetMembers()
					.Where(c => Regex.IsMatch(c.Name, syncSymbol.Name + "Async" + @"(`[0-9])?"))
					.OfType<IMethodSymbol>()
					.ToList();

				// First attempt to find an async counterpart method accepting a cancellation token.
				foreach (var candidate in asyncCandidates.Where(c => c.Parameters.Length == (syncSymbol.IsExtensionMethod ? syncSymbol.Parameters.Length + 2 : syncSymbol.Parameters.Length + 1)))
                {
					var ctPos = candidate.Parameters.TakeWhile(p => p.Type != _cancellationTokenSymbol).Count();

	                if (ctPos == candidate.Parameters.Length)  // No cancellation token
                        continue;

					var parameters = candidate.Parameters;

	                if (syncSymbol.IsExtensionMethod)
	                {
		                parameters = parameters.RemoveAt(ctPos).RemoveAt(0);
		                ctPos--;
	                }
	                else
	                {
		                parameters = parameters.RemoveAt(ctPos);
	                }

					if (!parameters.SequenceEqual(syncSymbol.Parameters, _paramComparer))
                        continue;

					cancellationTokenPos = ctPos;
                }

                if (cancellationTokenPos == -1)
                {
                    // Couldn't find an async overload that accepts a cancellation token.
                    // Next attempt to find an async method with a matching parameter list with no cancellation token
                    if (asyncCandidates.Any(ms =>
                            ms.Parameters.Length == (syncSymbol.IsExtensionMethod ? syncSymbol.Parameters.Length + 1 : syncSymbol.Parameters.Length) &&
							(syncSymbol.IsExtensionMethod ? ms.Parameters.Skip(1) : ms.Parameters).SequenceEqual(syncSymbol.Parameters, _paramComparer)
                    ))
                    {
                        cancellationTokenPos = -1;
                    }
                    else
                    {
						// Couldn't find anything, don't rewrite the invocation
						return node;
                    }
                }
            }

            _log.Info("    Found rewritable invocation: " + syncSymbol);

            var rewritten = RewriteExpression(node, cancellationTokenPos);
            if (!(node.Parent is StatementSyntax))
                rewritten = SyntaxFactory.ParenthesizedExpression(rewritten);
            return rewritten;
        }

        ExpressionSyntax RewriteExpression(InvocationExpressionSyntax node, int cancellationTokenPos)
        {
            InvocationExpressionSyntax rewrittenInvocation = null;

            if (node.Expression is IdentifierNameSyntax)
            {
                var identifierName = (IdentifierNameSyntax)node.Expression;
                rewrittenInvocation = node.WithExpression(identifierName.WithIdentifier(
                    SyntaxFactory.Identifier(identifierName.Identifier.Text + "Async")
                ));
            }
            else if (node.Expression is MemberAccessExpressionSyntax)
            {
                var memberAccessExp = (MemberAccessExpressionSyntax)node.Expression;
                var nestedInvocation = memberAccessExp.Expression as InvocationExpressionSyntax;
                if (nestedInvocation != null)
                    memberAccessExp = memberAccessExp.WithExpression((ExpressionSyntax)VisitInvocationExpression(nestedInvocation));

                rewrittenInvocation = node.WithExpression(memberAccessExp.WithName(
                    memberAccessExp.Name.WithIdentifier(
                        SyntaxFactory.Identifier(memberAccessExp.Name.Identifier.Text + "Async")
                    )
                ));
            }
            else if (node.Expression is GenericNameSyntax)
            {
                var genericNameExp = (GenericNameSyntax)node.Expression;
                rewrittenInvocation = node.WithExpression(
                    genericNameExp.WithIdentifier(SyntaxFactory.Identifier(genericNameExp.Identifier.Text + "Async"))
                );
            }
			else throw new NotSupportedException($"It seems there's an expression type ({node.Expression.GetType().Name}) not yet supported by the AsyncRewriter");

            if (cancellationTokenPos != -1)
            {
                var cancellationTokenArg = SyntaxFactory.Argument(SyntaxFactory.IdentifierName("cancellationToken"));

                if (cancellationTokenPos == rewrittenInvocation.ArgumentList.Arguments.Count)
                    rewrittenInvocation = rewrittenInvocation.WithArgumentList(
                        rewrittenInvocation.ArgumentList.AddArguments(cancellationTokenArg)
                    );
                else
                    rewrittenInvocation = rewrittenInvocation.WithArgumentList(SyntaxFactory.ArgumentList(
                        rewrittenInvocation.ArgumentList.Arguments.Insert(cancellationTokenPos, cancellationTokenArg)
                    ));
            }
			
			var methodInvocation = SyntaxFactory.InvocationExpression
			(
				SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, rewrittenInvocation, SyntaxFactory.IdentifierName("ConfigureAwait")),
				SyntaxFactory.ArgumentList(new SeparatedSyntaxList<ArgumentSyntax>().Add(SyntaxFactory.Argument(SyntaxFactory.ParseExpression("false"))))
			);

			return SyntaxFactory.AwaitExpression(methodInvocation);
        }

        class ParameterComparer : IEqualityComparer<IParameterSymbol>
        {
            public bool Equals(IParameterSymbol x, IParameterSymbol y)
            {
                return
                    x.Name.Equals(y.Name) &&
                    x.Type.Equals(y.Type);
            }

            public int GetHashCode(IParameterSymbol p)
            {
                return p.GetHashCode();
            }
        }
    }
}
