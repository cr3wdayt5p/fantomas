﻿module internal rec Fantomas.Core.CodePrinter2

open System
open Fantomas.Core.Context
open Fantomas.Core.SyntaxOak

let rec (|UppercaseType|LowercaseType|) (t: Type) : Choice<unit, unit> =
    let upperOrLower (v: string) =
        let isUpper = Seq.tryHead v |> Option.map Char.IsUpper |> Option.defaultValue false
        if isUpper then UppercaseType else LowercaseType

    match t with
    | Type.LongIdent node ->
        let lastIdent =
            List.tryFindBack
                (function
                | IdentifierOrDot.Ident _ -> true
                | _ -> false)
                node.Content

        match lastIdent with
        | Some (IdentifierOrDot.Ident ident) -> upperOrLower ident.Text
        | _ -> LowercaseType
    | Type.Var node -> upperOrLower node.Text
    | Type.AppPostfix node -> (|UppercaseType|LowercaseType|) node.First
    | Type.AppPrefix node -> (|UppercaseType|LowercaseType|) node.Identifier
    | _ -> failwithf $"Cannot determine if synType %A{t} is uppercase or lowercase"

let genTrivia (trivia: TriviaNode) (ctx: Context) =
    let currentLastLine = ctx.WriterModel.Lines |> List.tryHead

    // Some items like #if or Newline should be printed on a newline
    // It is hard to always get this right in CodePrinter, so we detect it based on the current code.
    let addNewline =
        currentLastLine
        |> Option.map (fun line -> line.Trim().Length > 0)
        |> Option.defaultValue false

    let addSpace =
        currentLastLine
        |> Option.bind (fun line -> Seq.tryLast line |> Option.map (fun lastChar -> lastChar <> ' '))
        |> Option.defaultValue false

    let gen =
        match trivia.Content with
        | LineCommentAfterSourceCode s ->
            let comment = sprintf "%s%s" (if addSpace then " " else String.empty) s
            writerEvent (WriteBeforeNewline comment)
        | CommentOnSingleLine comment -> (ifElse addNewline sepNlnForTrivia sepNone) +> !-comment +> sepNlnForTrivia
        | Newline -> (ifElse addNewline (sepNlnForTrivia +> sepNlnForTrivia) sepNlnForTrivia)

    gen ctx

let enterNode<'n when 'n :> Node> (n: 'n) = col sepNone n.ContentBefore genTrivia
let leaveNode<'n when 'n :> Node> (n: 'n) = col sepNone n.ContentAfter genTrivia
let genNode<'n when 'n :> Node> (n: 'n) (f: Context -> Context) = enterNode n +> f +> leaveNode n

let genSingleTextNode (node: SingleTextNode) = !-node.Text |> genNode node
let genSingleTextNodeWithLeadingDot (node: SingleTextNode) = !- $".{node.Text}" |> genNode node

let genMultipleTextsNode (node: MultipleTextsNode) =
    col sepSpace node.Content genSingleTextNode

let genIdentListNodeAux addLeadingDot (iln: IdentListNode) =
    col sepNone iln.Content (fun identOrDot ->
        match identOrDot with
        | IdentifierOrDot.Ident ident ->
            if addLeadingDot then
                genSingleTextNodeWithLeadingDot ident
            else
                genSingleTextNode ident
        | IdentifierOrDot.KnownDot _
        | IdentifierOrDot.UnknownDot _ -> sepDot)
    |> genNode iln

let genIdentListNode iln = genIdentListNodeAux false iln
let genIdentListNodeWithDot iln = genIdentListNodeAux true iln

let genAccessOpt (nodeOpt: SingleTextNode option) =
    match nodeOpt with
    | None -> sepNone
    | Some node -> genSingleTextNode node

let addSpaceBeforeParenInPattern (node: IdentListNode) (ctx: Context) =
    node.Content
    |> List.tryFindBack (function
        | IdentifierOrDot.Ident node -> not (String.IsNullOrWhiteSpace node.Text)
        | _ -> false)
    |> fun identOrDot ->
        match identOrDot with
        | Some (IdentifierOrDot.Ident node) ->
            let parameterValue =
                if Char.IsUpper node.Text.[0] then
                    ctx.Config.SpaceBeforeUppercaseInvocation
                else
                    ctx.Config.SpaceBeforeLowercaseInvocation

            onlyIf parameterValue sepSpace ctx
        | _ -> sepSpace ctx

let genParsedHashDirective (phd: ParsedHashDirectiveNode) =
    !- "#" +> !-phd.Ident +> sepSpace +> col sepSpace phd.Args genSingleTextNode
    |> genNode phd

let genUnit (n: UnitNode) =
    genSingleTextNode n.OpeningParen +> genSingleTextNode n.ClosingParen

// genNode will should be called in the caller function.
let genConstant (c: Constant) =
    match c with
    | Constant.FromText n -> genSingleTextNode n
    | Constant.Unit n -> genUnit n

let genAttributesCore (ats: AttributeNode list) =
    let genAttributeExpr (attr: AttributeNode) =
        match attr.Expr with
        | None -> opt sepColon attr.Target genSingleTextNode +> genIdentListNode attr.TypeName
        | Some e ->
            let argSpacing = if e.HasParentheses then sepNone else sepSpace

            opt sepColon attr.Target genSingleTextNode
            +> genIdentListNode attr.TypeName
            +> argSpacing
            +> genExpr e
        |> genNode attr

    let shortExpression = atCurrentColumn (col sepSemi ats genAttributeExpr)
    let longExpression = atCurrentColumn (col (sepSemi +> sepNln) ats genAttributeExpr)
    ifElse ats.IsEmpty sepNone (expressionFitsOnRestOfLine shortExpression longExpression)

let genOnelinerAttributes (n: MultipleAttributeListNode) =
    let ats =
        List.collect (fun (al: AttributeListNode) -> al.Attributes) n.AttributeLists

    let openingToken =
        List.tryHead n.AttributeLists
        |> Option.map (fun (a: AttributeListNode) -> a.Opening)

    let closingToken =
        List.tryLast n.AttributeLists
        |> Option.map (fun (a: AttributeListNode) -> a.Closing)

    let genAttrs =
        optSingle genSingleTextNode openingToken
        +> genAttributesCore ats
        +> optSingle genSingleTextNode closingToken
        |> genNode n

    ifElse ats.IsEmpty sepNone (genAttrs +> sepSpace)

let genAttributes (node: MultipleAttributeListNode) =
    colPost sepNlnUnlessLastEventIsNewline sepNln node.AttributeLists (fun a ->
        genSingleTextNode a.Opening
        +> (genAttributesCore a.Attributes)
        +> genSingleTextNode a.Closing
        +> sepNlnWhenWriteBeforeNewlineNotEmpty)

let genExpr (e: Expr) =
    match e with
    | Expr.Lazy node ->
        let genInfixExpr (ctx: Context) =
            isShortExpression
                ctx.Config.MaxInfixOperatorExpression
                // if this fits on the rest of line right after the lazy keyword, it should be wrapped in parenthesis.
                (sepOpenT +> genExpr node.Expr +> sepCloseT)
                // if it is multiline there is no need for parenthesis, because of the indentation
                (indent +> sepNln +> genExpr node.Expr +> unindent)
                ctx

        let genNonInfixExpr =
            autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr node.Expr)

        genSingleTextNode node.LazyWord
        +> sepSpaceUnlessWriteBeforeNewlineNotEmpty
        +> ifElse node.ExprIsInfix genInfixExpr genNonInfixExpr
    | Expr.Single node ->
        genSingleTextNode node.Leading
        +> sepSpace
        +> ifElse
            node.SupportsStroustrup
            (autoIndentAndNlnIfExpressionExceedsPageWidthUnlessStroustrup genExpr node.Expr)
            (autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr node.Expr))
    | Expr.Constant node -> genConstant node
    | Expr.Null node -> genSingleTextNode node
    | Expr.Quote node -> genQuoteExpr node
    | Expr.Typed node ->
        let short =
            genExpr node.Expr
            +> sepSpace
            +> !-node.Operator
            +> sepSpace
            +> genType node.Type

        let long =
            genExpr node.Expr +> sepNln +> !-node.Operator +> sepSpace +> genType node.Type

        match node.Expr with
        | Expr.Lambda _ -> long
        | _ -> expressionFitsOnRestOfLine short long
    | Expr.NewParen node ->
        let sepSpaceBeforeArgs (ctx: Context) =
            match node.Type with
            | UppercaseType -> onlyIf ctx.Config.SpaceBeforeUppercaseInvocation sepSpace ctx
            | LowercaseType -> onlyIf ctx.Config.SpaceBeforeLowercaseInvocation sepSpace ctx

        let short =
            genSingleTextNode node.NewKeyword
            +> sepSpace
            +> genType node.Type
            +> sepSpaceBeforeArgs
            +> genExpr node.Arguments

        let long =
            genSingleTextNode node.NewKeyword
            +> sepSpace
            +> genType node.Type
            +> sepSpaceBeforeArgs
            +> genMultilineFunctionApplicationArguments node.Arguments

        expressionFitsOnRestOfLine short long
    | Expr.New _ -> failwith "Not Implemented"
    | Expr.Tuple _ -> failwith "Not Implemented"
    | Expr.StructTuple _ -> failwith "Not Implemented"
    | Expr.ArrayOrList node ->
        if node.Elements.IsEmpty then
            genSingleTextNode node.Opening +> genSingleTextNode node.Closing
        else
            let smallExpression =
                genSingleTextNode node.Opening
                +> ifSpaceAroundDelimiter
                +> col sepSemi node.Elements genExpr
                +> ifSpaceAroundDelimiter
                +> genSingleTextNode node.Closing

            let multilineExpression =
                let genMultiLineArrayOrListAlignBrackets =
                    genSingleTextNode node.Opening
                    +> indent
                    +> sepNlnUnlessLastEventIsNewline
                    +> col sepNln node.Elements genExpr
                    +> unindent
                    +> sepNlnUnlessLastEventIsNewline
                    +> genSingleTextNode node.Closing

                let genMultiLineArrayOrList =
                    genSingleTextNode node.Opening
                    +> ifSpaceAroundDelimiter
                    +> atCurrentColumnIndent (
                        sepNlnWhenWriteBeforeNewlineNotEmpty
                        +> col sepNln node.Elements genExpr
                        +> (enterNode node.Closing
                            +> (fun ctx ->
                                let isFixed = lastWriteEventIsNewline ctx
                                (onlyIfNot isFixed sepSpace +> leaveNode node.Closing) ctx))
                    )

                ifAlignBrackets genMultiLineArrayOrListAlignBrackets genMultiLineArrayOrList

            fun ctx ->
                let alwaysMultiline = false
                // List.exists isIfThenElseWithYieldReturn xs
                // || List.forall isSynExprLambdaOrIfThenElse xs
                if alwaysMultiline then
                    multilineExpression ctx
                else
                    let size = getListOrArrayExprSize ctx ctx.Config.MaxArrayOrListWidth node.Elements
                    isSmallExpression size smallExpression multilineExpression ctx
    | Expr.Record _ -> failwith "Not Implemented"
    | Expr.AnonRecord _ -> failwith "Not Implemented"
    | Expr.ObjExpr _ -> failwith "Not Implemented"
    | Expr.While _ -> failwith "Not Implemented"
    | Expr.For _ -> failwith "Not Implemented"
    | Expr.ForEach _ -> failwith "Not Implemented"
    | Expr.NamedComputation _ -> failwith "Not Implemented"
    | Expr.Computation _ -> failwith "Not Implemented"
    | Expr.CompExprBody _ -> failwith "Not Implemented"
    | Expr.JoinIn _ -> failwith "Not Implemented"
    | Expr.ParenLambda _ -> failwith "Not Implemented"
    | Expr.Lambda _ -> failwith "Not Implemented"
    | Expr.MatchLambda _ -> failwith "Not Implemented"
    | Expr.Match _ -> failwith "Not Implemented"
    | Expr.TraitCall _ -> failwith "Not Implemented"
    | Expr.ParenILEmbedded _ -> failwith "Not Implemented"
    | Expr.ParenFunctionNameWithStar _ -> failwith "Not Implemented"
    | Expr.Paren node ->
        genSingleTextNode node.OpeningParen
        +> genExpr node.Expr
        +> genSingleTextNode node.ClosingParen
    | Expr.Dynamic _ -> failwith "Not Implemented"
    | Expr.PrefixApp _ -> failwith "Not Implemented"
    | Expr.NewlineInfixAppAlwaysMultiline _ -> failwith "Not Implemented"
    | Expr.NewlineInfixApps _ -> failwith "Not Implemented"
    | Expr.SameInfixApps _ -> failwith "Not Implemented"
    | Expr.InfixApp _ -> failwith "nope"
    | Expr.TernaryApp _ -> failwith "Not Implemented"
    | Expr.IndexWithoutDot _ -> failwith "Not Implemented"
    | Expr.AppDotGetTypeApp _ -> failwith "Not Implemented"
    | Expr.DotGetAppDotGetAppParenLambda _ -> failwith "Not Implemented"
    | Expr.DotGetAppParen _ -> failwith "Not Implemented"
    | Expr.DotGetAppWithParenLambda _ -> failwith "Not Implemented"
    | Expr.DotGetApp _ -> failwith "Not Implemented"
    | Expr.AppLongIdentAndSingleParenArg _ -> failwith "Not Implemented"
    | Expr.AppSingleParenArg _ -> failwith "Not Implemented"
    | Expr.DotGetAppWithLambda _ -> failwith "Not Implemented"
    | Expr.AppWithLambda _ -> failwith "Not Implemented"
    | Expr.NestedIndexWithoutDot _ -> failwith "Not Implemented"
    | Expr.EndsWithDualListApp node ->
        fun ctx ->
            // check if everything else beside the last array/list fits on one line
            let singleLineTestExpr =
                genExpr node.FunctionExpr
                +> sepSpace
                +> col sepSpace node.SequentialExpr genExpr
                +> sepSpace
                +> genExpr node.FirstArrayOrList

            let short =
                genExpr node.FunctionExpr
                +> sepSpace
                +> col sepSpace node.SequentialExpr genExpr
                +> onlyIfNot node.SequentialExpr.IsEmpty sepSpace
                +> genExpr node.FirstArrayOrList
                +> sepSpace
                +> genExpr node.LastArrayOrList

            let long =
                // check if everything besides both lists fits on one line
                let singleLineTestExpr =
                    genExpr node.FunctionExpr
                    +> sepSpace
                    +> col sepSpace node.SequentialExpr genExpr

                if futureNlnCheck singleLineTestExpr ctx then
                    genExpr node.FunctionExpr
                    +> indent
                    +> sepNln
                    +> col sepNln node.SequentialExpr genExpr
                    +> sepSpace
                    +> genExpr node.FirstArrayOrList
                    +> sepSpace
                    +> genExpr node.LastArrayOrList
                    +> unindent
                else
                    genExpr node.FunctionExpr
                    +> sepSpace
                    +> col sepSpace node.SequentialExpr genExpr
                    +> genExpr node.FirstArrayOrList
                    +> sepSpace
                    +> genExpr node.LastArrayOrList

            if futureNlnCheck singleLineTestExpr ctx then
                long ctx
            else
                short ctx
    | Expr.EndsWithSingleListApp node ->
        fun ctx ->
            // check if everything else beside the last array/list fits on one line
            let singleLineTestExpr =
                genExpr node.FunctionExpr
                +> sepSpace
                +> col sepSpace node.SequentialExpr genExpr

            let short =
                genExpr node.FunctionExpr
                +> sepSpace
                +> col sepSpace node.SequentialExpr genExpr
                +> onlyIfNot node.SequentialExpr.IsEmpty sepSpace
                +> genExpr node.ArrayOrList

            let long =
                genExpr node.FunctionExpr
                +> indent
                +> sepNln
                +> col sepNln node.SequentialExpr genExpr
                +> onlyIfNot node.SequentialExpr.IsEmpty sepNln
                +> genExpr node.ArrayOrList
                +> unindent

            if futureNlnCheck singleLineTestExpr ctx then
                long ctx
            else
                short ctx
    | Expr.App _ -> failwith "Not Implemented"
    | Expr.TypeApp _ -> failwith "Not Implemented"
    | Expr.LetOrUses _ -> failwith "Not Implemented"
    | Expr.TryWithSingleClause _ -> failwith "Not Implemented"
    | Expr.TryWith _ -> failwith "Not Implemented"
    | Expr.TryFinally _ -> failwith "Not Implemented"
    | Expr.Sequentials _ -> failwith "Not Implemented"
    | Expr.IfThen _ -> failwith "Not Implemented"
    | Expr.IfThenElse _ -> failwith "Not Implemented"
    | Expr.IfThenElif _ -> failwith "Not Implemented"
    | Expr.Ident node -> genSingleTextNode node
    | Expr.OptVar _ -> failwith "Not Implemented"
    | Expr.LongIdentSet _ -> failwith "Not Implemented"
    | Expr.DotIndexedGet _ -> failwith "Not Implemented"
    | Expr.DotIndexedSet _ -> failwith "Not Implemented"
    | Expr.NamedIndexedPropertySet _ -> failwith "Not Implemented"
    | Expr.DotNamedIndexedPropertySet _ -> failwith "Not Implemented"
    | Expr.DotGet _ -> failwith "Not Implemented"
    | Expr.DotSet _ -> failwith "Not Implemented"
    | Expr.Set _ -> failwith "Not Implemented"
    | Expr.LibraryOnlyStaticOptimization _ -> failwith "Not Implemented"
    | Expr.InterpolatedStringExpr _ -> failwith "Not Implemented"
    | Expr.IndexRangeWildcard _ -> failwith "Not Implemented"
    | Expr.IndexRange _ -> failwith "Not Implemented"
    | Expr.IndexFromEnd _ -> failwith "Not Implemented"
    | Expr.Typar _ -> failwith "Not Implemented"
    |> genNode (Expr.Node e)

let genQuoteExpr (node: ExprQuoteNode) =
    genSingleTextNode node.OpenToken
    +> sepSpace
    +> expressionFitsOnRestOfLine (genExpr node.Expr) (indent +> sepNln +> genExpr node.Expr +> unindent +> sepNln)
    +> sepSpace
    +> genSingleTextNode node.CloseToken

let genMultilineFunctionApplicationArguments (argExpr: Expr) = !- "todo!"
// let argsInsideParenthesis lpr rpr pr f =
//     sepOpenTFor lpr +> indentSepNlnUnindent f +> sepNln +> sepCloseTFor rpr
//     |> genTriviaFor SynExpr_Paren pr
//
// let genExpr e =
//     match e with
//     | InfixApp (equal, operatorSli, e1, e2, range) when (equal = "=") ->
//         genNamedArgumentExpr operatorSli e1 e2 range
//     | _ -> genExpr e
//
// match argExpr with
// | Paren (lpr, Lambda (pats, arrowRange, body, range), rpr, _pr) ->
//     fun ctx ->
//         if ctx.Config.MultiLineLambdaClosingNewline then
//             let genPats =
//                 let shortPats = col sepSpace pats genPat
//                 let longPats = atCurrentColumn (sepNln +> col sepNln pats genPat)
//                 expressionFitsOnRestOfLine shortPats longPats
//
//             (sepOpenTFor lpr
//              +> (!- "fun " +> genPats +> genLambdaArrowWithTrivia genExpr body arrowRange
//                  |> genTriviaFor SynExpr_Lambda range)
//              +> sepNln
//              +> sepCloseTFor rpr)
//                 ctx
//         else
//             genExpr argExpr ctx
// | Paren (lpr, Tuple (args, tupleRange), rpr, pr) ->
//     genTupleMultiline args
//     |> genTriviaFor SynExpr_Tuple tupleRange
//     |> argsInsideParenthesis lpr rpr pr
// | Paren (lpr, singleExpr, rpr, pr) -> genExpr singleExpr |> argsInsideParenthesis lpr rpr pr
// | _ -> genExpr argExpr

let genPatLeftMiddleRight (node: PatLeftMiddleRight) =
    genPat node.LeftHandSide
    +> sepSpace
    +> (match node.Middle with
        | Choice1Of2 node -> genSingleTextNode node
        | Choice2Of2 text -> !-text)
    +> sepSpace
    +> genPat node.RightHandSide

let genTyparDecls (td: TyparDecls) = !- "todo"

let genPat (p: Pattern) =
    match p with
    | Pattern.OptionalVal n -> genSingleTextNode n
    | Pattern.Attrib node -> genOnelinerAttributes node.Attributes +> genPat node.Pattern
    | Pattern.Or node -> genPatLeftMiddleRight node
    | Pattern.Ands node -> col (!- " & ") node.Patterns genPat
    | Pattern.Null node
    | Pattern.Wild node -> genSingleTextNode node
    | Pattern.Typed node ->
        genPat node.Pattern
        +> sepColon
        +> autoIndentAndNlnIfExpressionExceedsPageWidth (atCurrentColumnIndent (genType node.Type))
    | Pattern.Named node -> genSingleTextNode node.Name
    | Pattern.As node
    | Pattern.ListCons node -> genPatLeftMiddleRight node
    | Pattern.NamePatPairs node ->
        let genPatWithIdent (node: NamePatPair) =
            genSingleTextNode node.Ident
            +> sepSpace
            +> genSingleTextNode node.Equals
            +> sepSpace
            +> genPat node.Pattern

        let pats =
            expressionFitsOnRestOfLine
                (atCurrentColumn (col sepSemi node.Pairs genPatWithIdent))
                (atCurrentColumn (col sepNln node.Pairs genPatWithIdent))

        genIdentListNode node.Identifier
        +> optSingle genTyparDecls node.TyparDecls
        +> addSpaceBeforeParenInPattern node.Identifier
        +> genSingleTextNode node.OpeningParen
        +> autoIndentAndNlnIfExpressionExceedsPageWidth (sepNlnWhenWriteBeforeNewlineNotEmpty +> pats)
        +> genSingleTextNode node.ClosingParen

    | Pattern.LongIdent node ->
        let genName =
            genAccessOpt node.Accessibility
            +> genIdentListNode node.Identifier
            +> optSingle genTyparDecls node.TyparDecls

        let genParameters =
            match node.Parameters with
            | [] -> sepNone
            | [ Pattern.Paren _ as parameter ] -> addSpaceBeforeParenInPattern node.Identifier +> genPat parameter
            | ps -> sepSpace +> atCurrentColumn (col sepSpace ps genPat)

        genName +> genParameters
    | Pattern.Unit n -> genUnit n
    | Pattern.Paren node ->
        genSingleTextNode node.OpeningParen
        +> genPat node.Pattern
        +> genSingleTextNode node.ClosingParen
    | Pattern.Tuple node ->
        expressionFitsOnRestOfLine
            (col sepComma node.Patterns genPat)
            (atCurrentColumn (col (sepComma +> sepNln) node.Patterns genPat))
    | Pattern.StructTuple node ->
        !- "struct "
        +> sepOpenT
        +> atCurrentColumn (colAutoNlnSkip0 sepComma node.Patterns genPat)
        +> sepCloseT
    | Pattern.ArrayOrList node ->
        let genPats =
            let short = colAutoNlnSkip0 sepSemi node.Patterns genPat
            let long = col sepNln node.Patterns genPat
            expressionFitsOnRestOfLine short long

        ifElse
            node.Patterns.IsEmpty
            (genSingleTextNode node.OpenToken +> genSingleTextNode node.CloseToken)
            (genSingleTextNode node.OpenToken
             +> ifSpaceAroundDelimiter
             +> atCurrentColumn genPats
             +> ifSpaceAroundDelimiter
             +> genSingleTextNode node.CloseToken)
    | Pattern.Record node ->
        let smallRecordExpr =
            genSingleTextNode node.OpeningNode
            +> ifSpaceAroundDelimiter
            +> col sepSemi node.Fields genPatRecordFieldName
            +> ifSpaceAroundDelimiter
            +> genSingleTextNode node.ClosingNode

        let multilineRecordExpr =
            genSingleTextNode node.OpeningNode
            +> ifSpaceAroundDelimiter
            +> atCurrentColumn (col sepNln node.Fields genPatRecordFieldName)
            +> ifSpaceAroundDelimiter
            +> genSingleTextNode node.ClosingNode

        let multilineRecordExprAlignBrackets =
            genSingleTextNode node.OpeningNode
            +> indent
            +> sepNln
            +> atCurrentColumn (col sepNln node.Fields genPatRecordFieldName)
            +> unindent
            +> sepNln
            +> genSingleTextNode node.ClosingNode
            |> atCurrentColumnIndent

        let multilineExpressionIfAlignBrackets =
            ifAlignBrackets multilineRecordExprAlignBrackets multilineRecordExpr

        fun ctx ->
            let size = getRecordSize ctx node.Fields
            isSmallExpression size smallRecordExpr multilineExpressionIfAlignBrackets ctx
    | Pattern.Const c -> genConstant c
    | Pattern.IsInst node -> genSingleTextNode node.Token +> sepSpace +> genType node.Type
    | Pattern.QuoteExpr node -> genQuoteExpr node
    |> genNode (Pattern.Node p)

let genPatRecordFieldName (node: PatRecordField) =
    match node.Prefix with
    | None ->
        genSingleTextNode node.FieldName
        +> sepSpace
        +> genSingleTextNode node.Equals
        +> sepSpace
        +> genPat node.Pattern
    | Some prefix ->
        genIdentListNode prefix
        +> sepDot
        +> genSingleTextNode node.FieldName
        +> sepSpace
        +> genSingleTextNode node.Equals
        +> sepSpace
        +> genPat node.Pattern

let genBinding (b: BindingNode) =
    let genParameters =
        match b.Parameters with
        | [] -> sepNone
        | ps -> sepSpace +> col sepSpace ps genPat +> sepSpace

    let genReturnType =
        match b.ReturnType with
        | None -> sepNone
        | Some (colon, t) -> genSingleTextNode colon +> sepSpace +> genType t

    genMultipleTextsNode b.LeadingKeyword
    +> sepSpace
    +> (match b.FunctionName with
        | Choice1Of2 n -> genSingleTextNode n
        | Choice2Of2 pat -> genPat pat)
    +> genParameters
    +> genReturnType
    +> sepSpace
    +> genSingleTextNode b.Equals
    +> sepSpace
    +> genExpr b.Expr
    |> genNode b

let genOpenList (openList: OpenListNode) =
    col sepNln openList.Opens (function
        | Open.ModuleOrNamespace node -> !- "open " +> genIdentListNode node.Name |> genNode node
        | Open.Target node -> !- "open type " +> genType node.Target)

let genTypeConstraint (tc: TypeConstraint) =
    match tc with
    | TypeConstraint.Single node -> genSingleTextNode node.Typar +> sepColon +> genSingleTextNode node.Kind
    | TypeConstraint.DefaultsToType node ->
        genSingleTextNode node.Default
        +> sepSpace
        +> genSingleTextNode node.Typar
        +> sepColon
        +> genType node.Type
    | TypeConstraint.SubtypeOfType node -> genSingleTextNode node.Typar +> !- " :> " +> genType node.Type
    | TypeConstraint.SupportsMember _ -> failwith "todo!"
    | TypeConstraint.EnumOrDelegate node ->
        genSingleTextNode node.Typar
        +> sepColon
        +> !- $"{node.Verb}<"
        +> col sepComma node.Types genType
        +> !- ">"
    | TypeConstraint.WhereSelfConstrained t -> genType t

let genTypeConstraints (tcs: TypeConstraint list) =
    !- "when" +> sepSpace +> col wordAnd tcs genTypeConstraint

let genType (t: Type) =
    match t with
    | Type.Funs node ->
        let short =
            col sepNone node.Parameters (fun (t, arrow) ->
                genType t
                +> sepSpace
                +> genSingleTextNode arrow
                +> sepSpace
                +> sepNlnWhenWriteBeforeNewlineNotEmpty)
            +> genType node.ReturnType

        let long =
            match node.Parameters with
            | [] -> genType node.ReturnType
            | (ht, ha) :: rest ->
                genType ht
                +> indentSepNlnUnindent (
                    genSingleTextNode ha
                    +> sepSpace
                    +> col sepNone rest (fun (t, arrow) -> genType t +> sepNln +> genSingleTextNode arrow +> sepSpace)
                    +> genType node.ReturnType
                )

        expressionFitsOnRestOfLine short long
    | Type.Tuple node -> genSynTupleTypeSegments node.Path
    | Type.HashConstraint node -> genSingleTextNode node.Hash +> genType node.Type
    | Type.MeasurePower node -> genType node.BaseMeasure +> !- "^" +> !-node.Exponent
    | Type.StaticConstant c -> genConstant c
    | Type.StaticConstantExpr node -> genSingleTextNode node.Const +> sepSpace +> genExpr node.Expr
    | Type.StaticConstantNamed node ->
        genType node.Identifier
        +> !- "="
        +> addSpaceIfSynTypeStaticConstantHasAtSignBeforeString node.Value
        +> genType node.Value
    | Type.Array node -> genType node.Type +> !- "[" +> rep (node.Rank - 1) (!- ",") +> !- "]"
    | Type.Anon node -> genSingleTextNode node
    | Type.Var node -> genSingleTextNode node
    | Type.AppPostfix node -> genType node.First +> sepSpace +> genType node.Last
    | Type.AppPrefix node ->
        let addExtraSpace =
            match node.Arguments with
            | [] -> sepNone
            | [ Type.Var node ] when node.Text.StartsWith "^" -> sepSpace
            | t :: _ -> addSpaceIfSynTypeStaticConstantHasAtSignBeforeString t

        genType node.Identifier
        +> optSingle genIdentListNodeWithDot node.PostIdentifier
        +> genSingleTextNode node.LessThen
        +> addExtraSpace
        +> col sepComma node.Arguments genType
        +> addExtraSpace
        +> genSingleTextNode node.GreaterThan
    | Type.StructTuple node ->
        genSingleTextNode node.Keyword
        +> sepSpace
        +> sepOpenT
        +> genSynTupleTypeSegments node.Path
        +> genSingleTextNode node.ClosingParen
    | Type.WithSubTypeConstraint tc -> genTypeConstraint tc
    | Type.WithGlobalConstraints node -> genType node.Type +> sepSpace +> genTypeConstraints node.TypeConstraints
    | Type.LongIdent idn -> genIdentListNode idn
    | Type.AnonRecord node -> genAnonRecordType node
    | Type.Paren node ->
        genSingleTextNode node.OpeningParen
        +> genType node.Type
        +> genSingleTextNode node.ClosingParen
    | Type.SignatureParameter node ->
        genOnelinerAttributes node.Attributes
        +> optSingle (fun id -> genSingleTextNode id +> sepColon) node.Identifier
        +> autoIndentAndNlnIfExpressionExceedsPageWidth (genType node.Type)
    | Type.Or node ->
        genType node.LeftHandSide
        +> sepSpace
        +> genSingleTextNode node.Or
        +> sepSpace
        +> genType node.RightHandSide
    |> genNode (Type.Node t)

let genSynTupleTypeSegments (path: Choice<Type, SingleTextNode> list) =
    let genTs addNewline =
        col sepSpace path (fun t ->
            match t with
            | Choice1Of2 t -> genType t
            | Choice2Of2 node -> genSingleTextNode node +> onlyIf addNewline sepNln)

    expressionFitsOnRestOfLine (genTs false) (genTs true)

let genAnonRecordType (node: TypeAnonRecordNode) =
    let genStruct =
        match node.Struct with
        | None -> sepNone
        | Some n -> genSingleTextNode n +> sepSpace

    let genOpening =
        match node.Opening with
        | None -> sepOpenAnonRecdFixed
        | Some n -> genSingleTextNode n

    let genAnonRecordFieldType (i, t) =
        genSingleTextNode i
        +> sepColon
        +> autoIndentAndNlnIfExpressionExceedsPageWidth (genType t)

    let smallExpression =
        genStruct
        +> genOpening
        +> col sepSemi node.Fields genAnonRecordFieldType
        +> sepCloseAnonRecd

    let longExpression =
        let genFields = col sepNln node.Fields genAnonRecordFieldType

        let genMultilineAnonRecordTypeAlignBrackets =
            let genRecord =
                sepOpenAnonRecdFixed
                +> indentSepNlnUnindent (atCurrentColumnIndent genFields)
                +> sepNln
                +> sepCloseAnonRecdFixed

            genStruct +> genRecord

        let genMultilineAnonRecordType =
            let genRecord = sepOpenAnonRecd +> atCurrentColumn genFields +> sepCloseAnonRecd
            genStruct +> genRecord

        ifAlignBrackets genMultilineAnonRecordTypeAlignBrackets genMultilineAnonRecordType

    fun (ctx: Context) ->
        let size = getRecordSize ctx node.Fields
        isSmallExpression size smallExpression longExpression ctx

let addSpaceIfSynTypeStaticConstantHasAtSignBeforeString (t: Type) =
    match t with
    | Type.StaticConstant sc ->
        match sc with
        | Constant.FromText node -> onlyIf (node.Text.StartsWith("@")) sepSpace
        | _ -> sepNone
    | _ -> sepNone

let genTypeDefn (td: TypeDefn) =
    let header =
        let node = (TypeDefn.TypeDefnNode td).TypeName

        genSingleTextNode node.LeadingKeyword
        +> sepSpace
        +> genIdentListNode node.Identifier
        +> sepSpace
        +> optSingle genSingleTextNode node.EqualsToken

    let body =
        match td with
        | TypeDefn.Enum _ -> failwith "Not Implemented"
        | TypeDefn.Union _ -> failwith "Not Implemented"
        | TypeDefn.Record _ -> failwith "Not Implemented"
        | TypeDefn.None _ -> failwith "Not Implemented"
        | TypeDefn.Abbrev node -> genType node.Type
        | TypeDefn.Exception _ -> failwith "Not Implemented"
        | TypeDefn.ExplicitClassOrInterfaceOrStruct _ -> failwith "Not Implemented"
        | TypeDefn.Augmentation _ -> failwith "Not Implemented"
        | TypeDefn.Fun _ -> failwith "Not Implemented"
        | TypeDefn.Delegate _ -> failwith "Not Implemented"
        | TypeDefn.Unspecified _ -> failwith "Not Implemented"
        | TypeDefn.RegularType _ -> failwith "Not Implemented"

    leadingExpressionIsMultiline header (fun isMultiline ->
        ifElse isMultiline (indentSepNlnUnindent body) (sepSpace +> body))
    |> genNode (TypeDefn.Node td)

let genField (node: FieldNode) =
    optSingle genSingleTextNode node.XmlDoc
    +> genAttributes node.Attributes
    +> optSingle genMultipleTextsNode node.LeadingKeyword
    +> onlyIf node.IsMutable (!- "mutable ")
    +> genAccessOpt node.Accessibility
    +> opt sepColon node.Name genSingleTextNode
    +> autoIndentAndNlnIfExpressionExceedsPageWidth (genType node.Type)
    |> genNode node

let genUnionCase (hasVerticalBar: bool) (node: UnionCaseNode) =
    let shortExpr = col sepStar node.Fields genField

    let longExpr =
        indentSepNlnUnindent (atCurrentColumn (col (sepStar +> sepNln) node.Fields genField))

    let genBar =
        match node.Bar with
        | Some bar -> ifElse hasVerticalBar (genSingleTextNode bar +> sepSpace) (genNode bar sepNone)
        | None -> onlyIf hasVerticalBar sepBar

    optSingle genSingleTextNode node.XmlDoc
    +> genBar
    +> atCurrentColumn (
        // If the bar has a comment after, add a newline and print the identifier on the same column on the next line.
        sepNlnWhenWriteBeforeNewlineNotEmpty
        +> genOnelinerAttributes node.Attributes
        +> genSingleTextNode node.Identifier
        +> onlyIf (List.isNotEmpty node.Fields) wordOf
    )
    +> onlyIf (List.isNotEmpty node.Fields) (expressionFitsOnRestOfLine shortExpr longExpr)
    |> genNode node

let genMemberDefnList _ = !- "todo"

let genExceptionBody px ats ao uc =
    optSingle genSingleTextNode px
    +> genAttributes ats
    +> !- "exception "
    +> genAccessOpt ao
    +> genUnionCase false uc

let genException (node: ExceptionDefnNode) =
    genExceptionBody node.XmlDoc node.Attributes node.Accessibility node.UnionCase
    +> onlyIf
        (not node.Members.IsEmpty)
        (sepSpace
         +> optSingle genSingleTextNode node.WithKeyword
         +> indentSepNlnUnindent (genMemberDefnList node.Members))

let genModuleDecl (md: ModuleDecl) =
    match md with
    | ModuleDecl.OpenList ol -> genOpenList ol
    | ModuleDecl.HashDirectiveList node -> col sepNln node.HashDirectives genParsedHashDirective
    | ModuleDecl.Attributes node -> genAttributes node.Attributes +> genExpr node.Expr
    | ModuleDecl.DeclExpr e -> genExpr e
    | ModuleDecl.Exception node -> genException node
    | ModuleDecl.ExternBinding _ -> failwith "Not Implemented"
    | ModuleDecl.TopLevelBinding b -> genBinding b
    | ModuleDecl.ModuleAbbrev node ->
        genSingleTextNode node.Module
        +> sepSpace
        +> genSingleTextNode node.Name
        +> sepEqFixed
        +> sepSpace
        +> genIdentListNode node.Alias
    | ModuleDecl.NestedModule node -> optSingle genSingleTextNode node.XmlDoc +> genAttributes node.Attributes
    | ModuleDecl.TypeDefn td -> genTypeDefn td

let sepNlnUnlessContentBefore (node: Node) =
    if Seq.isEmpty node.ContentBefore then sepNln else sepNone

let colWithNlnWhenMappedNodeIsMultiline<'n> (mapNode: 'n -> Node) (f: 'n -> Context -> Context) (nodes: 'n list) =
    nodes
    |> List.map (fun n -> ColMultilineItem(f n, (mapNode >> sepNlnUnlessContentBefore) n))
    |> colWithNlnWhenItemIsMultiline

let colWithNlnWhenNodeIsMultiline<'n when 'n :> Node> (f: 'n -> Context -> Context) (nodes: 'n list) =
    colWithNlnWhenMappedNodeIsMultiline<'n> (fun n -> n :> Node) f nodes

let genModule (m: ModuleOrNamespaceNode) =
    onlyIf
        m.IsNamed
        (optSingle
            (fun (n: SingleTextNode) -> genSingleTextNode n +> sepSpace +> genIdentListNode m.Name)
            m.LeadingKeyword
         +> onlyIf (not m.Declarations.IsEmpty) (sepNln +> sepNln))
    +> colWithNlnWhenMappedNodeIsMultiline ModuleDecl.Node genModuleDecl m.Declarations
    |> genNode m

let genFile (oak: Oak) =
    col sepNln oak.ParsedHashDirectives genParsedHashDirective
    +> (if oak.ParsedHashDirectives.IsEmpty then sepNone else sepNln)
    +> col sepNln oak.ModulesOrNamespaces genModule
    +> (fun ctx -> onlyIf ctx.Config.InsertFinalNewline sepNln ctx)
    |> genNode oak