# Raven
### Universal Shader Compiler

Project is in it initial phase. Mostly as a research project.

## Overview

- Language is inspired by Typescript, C#, Kotlin and Stride shading language.
- Library's API is based on Roslyn.
- Targeting GLSL, SPIR-V, later HLSL and Metal.
- GLSL is the easiest to implement as it's just a transpiler.
- I have no idea how to do the semantic passes. LOL. It will be fun.
- Compiler should be able to generate an "interaction" classes such as stride do.
- Have some generator interface so other languages can be targeted as well.
- Package manager for shaders? Maybe a bit of overkill for the size of this project.
- But it will be easy to distribute shaders or shader libraries (math and stuff).


## Not supported compared to Roslyn

- goto_statement
- labeled_statement
- lock_statement
- throw_statement
- try_statement
- unsafe_statement
- yield_statement
- stack_alloc_array_creation_expression
- throw_expression

//    | function_pointer_type // TODO: not sure if this is possible to implement
//    | 'ref' 'readonly'? type #RefType // TODO: not sure if this is possible to implement
//    | scoped_type // TODO: same

## Usage

As a CLI tool
```
./raven compile --target glsl <input> <output>
```

As a library
```csharp
var text = File.ReadAllText("Shader.rvn");
var tree = SyntaxTree.ParseText(text, path: "Shader.rvn");

// Syntax Tree
var root = tree.GetRoot();

// Semantic model
var compilation = Compilation.Create("MyShaders", tree);

foreach (var diagnostic in compilation.GetDiagnostics()) {
    Console.WriteLine(diagnostic);   // Shader.rvn(12,9): error RVN2010: ...
}

var model = compilation.GetSemanticModel(tree);
var symbol = model.GetSymbolInfo(someExpression).Symbol;   // what a name refers to
var type = model.GetTypeInfo(someExpression).Type;         // what type it has

foreach (var entryPoint in compilation.GetEntryPoints()) {
    Console.WriteLine($"{entryPoint.Stage}: {entryPoint.ToDisplayString()}");
}

// TODO: lowering to IR, code generation, ...
```


## Language Example

```typescript
package Vixen.Test

import Vixen.Core
import Vixen.BaseShaders

// Test class
shader TestShader : ExampleBase, CustomShader {
    const val Multiplier = 42
    
    val len: int
    val test: FooBar = [1, 2, 3, 4]
    val test = [1, 2, 3, 4]
    
    var test: FooBar = [
        "string",
        'c',
        'a'
    ]
    
    init() {
        Test()
        
        for (i in 1..10) {
            a++
            ++a
            FooBar(i)
        }
        
        if (a > 42) {
            Call()
        } else {
            Not()
        }
    }
    
    init(test: string?) {
        long.SomeMethod()
        
        func test(): int { }
        len = CoreClass.GetLength<int, Class>(test)
        
        len = p[42]
        len = p[1..12]
    }
    
    func Generic<int>.Test<Asd>() { }
    
    func GetLength2(): int {
        return len
    }
    
    func GetLength() => len

    func VSMain() {
        var bar = 7 + 4 * "test" / 42f
        var foo = 7 + 4 * "test" / 42f
        var tst = 7 + 4 * "test" / 42f
        //Test();
    }
    
    func TestMethod(name: string, count: int = 42): float4 {
       // var test = "string";
      //  val res = name + test;
        
        //val hash = res.GetHashCode();
    
      //  return 42.3f;
    }
}


```
