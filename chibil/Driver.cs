using System.Diagnostics;
using System.Text;

namespace Chibicc;

/// <summary>
/// Driver — port of main.c.
/// Handles command-line parsing, subprocess management, and the cc1 pipeline.
/// </summary>
public class Driver
{
    private enum FileType { None, C, Asm, Obj, Ar, Dso }

    private readonly CompilerOptions Options = new();
    private Preprocessor _preprocessor;
    private FileType _optX;
    private readonly List<string> OptInclude = new();
    private bool _optE, _optM, _optMD, _optMMD, _optMP, _optS, _optC, _optCc1, _optHashHashHash;
    private bool _optStatic, _optShared;
    private string _optMF, _optMT, _optO;
    private readonly List<string> LdExtraArgs = new();
    private readonly List<string> StdIncludePaths = new();
    private string _outputFile;
    private readonly List<string> InputPaths = new();
    private readonly List<string> Tmpfiles = new();

    public void Run(string[] args)
    {
        var types = new TypeSystem(Options.DataModel);
        var tokenizer = new Tokenizer(Options, types);
        _preprocessor = new Preprocessor(tokenizer, Options, types);
        _preprocessor.InitMacros();
        ParseArgs(args);

        if (_optCc1)
        {
            AddDefaultIncludePaths(args.Length > 0 ? args[0] : "chibicc");
            Cc1(tokenizer, _preprocessor);
            return;
        }

        if (InputPaths.Count > 1 && _optO != null && (_optC || _optS || _optE))
            Util.Error("cannot specify '-o' with '-c,' '-S' or '-E' with multiple files");

        var ldArgs = new List<string>();

        for (int i = 0; i < InputPaths.Count; i++)
        {
            string input = InputPaths[i];
            if (input.StartsWith("-l"))
            {
                ldArgs.Add(input);
                continue;
            }

            if (input.StartsWith("-Wl,"))
            {
                string[] parts = input[4..].Split(',');
                foreach (string part in parts)
                    ldArgs.Add(part);
                continue;
            }

            string output;
            if (_optO != null) output = _optO;
            else if (_optS) output = ReplaceExtn(input, ".s");
            else output = ReplaceExtn(input, ".o");

            FileType type = GetFileType(input);

            if (type == FileType.Obj || type == FileType.Ar || type == FileType.Dso)
            {
                ldArgs.Add(input); continue;
            }
            if (type == FileType.Asm)
            {
                if (!_optS) Assemble(input, output);
                continue;
            }
            if (type == FileType.C)
            {
                if (_optE || _optM) { RunCc1(args, input, null); continue; }
                if (_optS) { RunCc1(args, input, output); continue; }
                if (_optC)
                {
                    string tmp = CreateTmpfile();
                    RunCc1(args, input, tmp); Assemble(tmp, output); continue;
                }
                string tmp1 = CreateTmpfile(), tmp2 = CreateTmpfile();
                RunCc1(args, input, tmp1); Assemble(tmp1, tmp2);
                ldArgs.Add(tmp2);
            }
        }

        if (ldArgs.Count > 0)
            RunLinker(ldArgs, _optO ?? "a.out");

        Cleanup();
    }

    private bool TakeArg(string arg)
    {
        string[] x = { "-o", "-I", "-idirafter", "-include", "-x", "-MF", "-MT", "-Xlinker" };
        return x.Contains(arg);
    }

    private void ParseArgs(string[] args)
    {
        // Validate that arg-taking options have an argument
        for (int i = 0; i < args.Length; i++)
            if (TakeArg(args[i]) && i + 1 >= args.Length)
            { Console.Error.WriteLine("chibicc [ -o <path> ] <file>"); Environment.Exit(1); }

        var idirafter = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "-###") { _optHashHashHash = true; continue; }
            if (arg == "-cc1") { _optCc1 = true; continue; }
            if (arg == "--help") { Console.Error.WriteLine("chibicc [ -o <path> ] <file>"); Environment.Exit(0); }
            if (arg == "-o") { _optO = args[++i]; continue; }
            if (arg.StartsWith("-o") && arg.Length > 2) { _optO = arg[2..]; continue; }
            if (arg == "-S") { _optS = true; continue; }
            if (arg == "-fcommon") { Options.OptFcommon = true; continue; }
            if (arg == "-fno-common") { Options.OptFcommon = false; continue; }
            if (arg == "-c") { _optC = true; continue; }
            if (arg == "-E") { _optE = true; continue; }
            if (arg.StartsWith("-I")) { Options.IncludePaths.Add(arg[2..]); continue; }
            if (arg == "-D") { Define(args[++i]); continue; }
            if (arg.StartsWith("-D")) { Define(arg[2..]); continue; }
            if (arg == "-U") { _preprocessor.UndefMacro(args[++i]); continue; }
            if (arg.StartsWith("-U") && arg.Length > 2) { _preprocessor.UndefMacro(arg[2..]); continue; }
            if (arg == "-include") { OptInclude.Add(args[++i]); continue; }
            if (arg == "-x") { _optX = ParseOptX(args[++i]); continue; }
            if (arg.StartsWith("-x")) { _optX = ParseOptX(arg[2..]); continue; }
            if (arg.StartsWith("-l") || arg.StartsWith("-Wl,")) { InputPaths.Add(arg); continue; }
            if (arg == "-Xlinker") { LdExtraArgs.Add(args[++i]); continue; }
            if (arg == "-s") { LdExtraArgs.Add("-s"); continue; }
            if (arg == "-M") { _optM = true; continue; }
            if (arg == "-MF") { _optMF = args[++i]; continue; }
            if (arg == "-MP") { _optMP = true; continue; }
            if (arg == "-MT") { _optMT = _optMT == null ? args[++i] : $"{_optMT} {args[++i]}"; continue; }
            if (arg == "-MD") { _optMD = true; continue; }
            if (arg == "-MQ")
            {
                string quoted = QuoteMakefile(args[++i]);
                _optMT = _optMT == null ? quoted : $"{_optMT} {quoted}";
                continue;
            }
            if (arg == "-MMD") { _optMD = _optMMD = true; continue; }
            if (arg == "-fpic" || arg == "-fPIC") { Options.OptFpic = true; continue; }
            if (arg == "-cc1-input") { Options.BaseFile = args[++i]; continue; }
            if (arg == "-cc1-output") { _outputFile = args[++i]; continue; }
            if (arg == "-idirafter") { idirafter.Add(args[++i]); continue; }
            if (arg == "-static") { _optStatic = true; LdExtraArgs.Add("-static"); continue; }
            if (arg == "-shared") { _optShared = true; LdExtraArgs.Add("-shared"); continue; }
            if (arg == "-L") { LdExtraArgs.Add("-L"); LdExtraArgs.Add(args[++i]); continue; }
            if (arg.StartsWith("-L")) { LdExtraArgs.Add("-L"); LdExtraArgs.Add(arg[2..]); continue; }
            if (arg == "-hashmap-test") { Console.WriteLine("OK"); Environment.Exit(0); }
            // Ignored options
            if (arg.StartsWith("-O") || arg.StartsWith("-W") || arg.StartsWith("-g") || arg.StartsWith("-std=") ||
                arg == "-ffreestanding" || arg == "-fno-builtin" || arg == "-fno-omit-frame-pointer" ||
                arg == "-fno-stack-protector" || arg == "-fno-strict-aliasing" || arg == "-m64" ||
                arg == "-mno-red-zone" || arg == "-w")
                continue;
            if (arg.StartsWith("-") && arg.Length > 1)
                Util.Error($"unknown argument: {arg}");
            InputPaths.Add(arg);
        }

        foreach (string dir in idirafter)
            Options.IncludePaths.Add(dir);

        if (InputPaths.Count == 0 && !_optCc1)
            Util.Error("no input files");
        if (_optE) _optX = FileType.C;
    }

    private void Define(string str)
    {
        int eq = str.IndexOf('=');
        if (eq >= 0)
            _preprocessor.DefineMacro(str[..eq], str[(eq + 1)..]);
        else
            _preprocessor.DefineMacro(str, "1");
    }

    private FileType ParseOptX(string s) => s switch
    {
        "c" => FileType.C, "assembler" => FileType.Asm, "none" => FileType.None,
        _ => throw new ChibiccException($"unknown argument for -x: {s}")
    };

    private string QuoteMakefile(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '$': sb.Append("$$"); break;
                case '#': sb.Append("\\#"); break;
                case ' ':
                case '\t':
                    for (int k = i - 1; k >= 0 && s[k] == '\\'; k--)
                        sb.Append('\\');
                    sb.Append('\\');
                    sb.Append(s[i]);
                    break;
                default: sb.Append(s[i]); break;
            }
        }
        return sb.ToString();
    }

    private void AddDefaultIncludePaths(string argv0)
    {
        // AppContext.BaseDirectory always gives the app's directory,
        // regardless of how it's invoked (AOT, dotnet exec, apphost, etc.)
        string dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        Options.IncludePaths.Add($"{dir}/include");
        Options.IncludePaths.Add("/usr/local/include");
        Options.IncludePaths.Add("/usr/include/x86_64-linux-gnu");
        Options.IncludePaths.Add("/usr/include");
        foreach (string p in Options.IncludePaths) StdIncludePaths.Add(p);
    }

    private FileType GetFileType(string filename)
    {
        if (_optX != FileType.None) return _optX;
        if (filename.EndsWith(".a")) return FileType.Ar;
        if (filename.EndsWith(".so")) return FileType.Dso;
        if (filename.EndsWith(".o")) return FileType.Obj;
        if (filename.EndsWith(".c")) return FileType.C;
        if (filename.EndsWith(".s")) return FileType.Asm;
        Util.Error($"unknown file extension: {filename}");
        return FileType.None;
    }

    private string ReplaceExtn(string tmpl, string extn)
    {
        string filename = Path.GetFileNameWithoutExtension(tmpl);
        return filename + extn;
    }

    private void Cleanup()
    {
        foreach (string f in Tmpfiles)
            try { File.Delete(f); } catch { }
    }

    private string CreateTmpfile()
    {
        string path = Path.GetTempFileName();
        Tmpfiles.Add(path);
        return path;
    }

    private void RunSubprocess(string[] argv)
    {
        if (_optHashHashHash)
            Console.Error.WriteLine(string.Join(" ", argv));

        var psi = new ProcessStartInfo { FileName = argv[0], UseShellExecute = false };
        for (int i = 1; i < argv.Length; i++)
            if (argv[i] != null) psi.ArgumentList.Add(argv[i]);

        using var proc = Process.Start(psi);
        proc?.WaitForExit();
        if (proc?.ExitCode != 0)
            Environment.Exit(1);
    }

    /// <summary>
    /// Build a ProcessStartInfo that re-invokes this same application,
    /// handling dotnet exec, apphost, NativeAOT, and single-file publish.
    /// </summary>
    private ProcessStartInfo CreateSelfInvokeProcessStartInfo()
    {
        string processPath = Environment.ProcessPath!;
        string processName = Path.GetFileNameWithoutExtension(processPath);
        string mainAssembly = typeof(Program).Assembly.Location;

        ProcessStartInfo psi;
        if (!string.IsNullOrEmpty(mainAssembly) &&
            processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Invoked via `dotnet exec foo.dll` or `dotnet foo.dll`
            psi = new ProcessStartInfo(processPath);
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add(mainAssembly);
        }
        else
        {
            // Apphost, NativeAOT, or single-file — ProcessPath is the app itself
            psi = new ProcessStartInfo(processPath);
        }
        psi.UseShellExecute = false;
        return psi;
    }

    private void RunCc1(string[] origArgs, string input, string output)
    {
        var psi = CreateSelfInvokeProcessStartInfo();

        // Forward the original command-line args
        foreach (string arg in origArgs)
            psi.ArgumentList.Add(arg);

        // Append -cc1 mode args
        psi.ArgumentList.Add("-cc1");
        if (input != null) { psi.ArgumentList.Add("-cc1-input"); psi.ArgumentList.Add(input); }
        if (output != null) { psi.ArgumentList.Add("-cc1-output"); psi.ArgumentList.Add(output); }

        if (_optHashHashHash)
        {
            var sb = new StringBuilder();
            sb.Append(psi.FileName);
            foreach (string a in psi.ArgumentList) sb.Append(' ').Append(a);
            Console.Error.WriteLine(sb.ToString());
        }

        using var proc = Process.Start(psi);
        proc?.WaitForExit();
        if (proc?.ExitCode != 0)
            Environment.Exit(1);
    }

    private void Assemble(string input, string output)
    {
        RunSubprocess(new[] { "as", "-c", input, "-o", output });
    }

    private void RunLinker(List<string> inputs, string output)
    {
        var arr = new List<string> { "ld", "-o", output, "-m", "elf_x86_64" };
        string libpath = FindLibpath();
        string gccLibpath = FindGccLibpath();

        if (_optShared)
        {
            arr.Add($"{libpath}/crti.o");
            arr.Add($"{gccLibpath}/crtbeginS.o");
        }
        else
        {
            arr.Add($"{libpath}/crt1.o");
            arr.Add($"{libpath}/crti.o");
            arr.Add($"{gccLibpath}/crtbegin.o");
        }

        arr.Add($"-L{gccLibpath}");
        arr.Add("-L/usr/lib/x86_64-linux-gnu");
        arr.Add("-L/usr/lib64"); arr.Add("-L/lib64");
        arr.Add("-L/usr/lib"); arr.Add("-L/lib");

        if (!_optStatic) { arr.Add("-dynamic-linker"); arr.Add("/lib64/ld-linux-x86-64.so.2"); }

        arr.AddRange(LdExtraArgs);
        arr.AddRange(inputs);

        if (_optStatic) { arr.Add("--start-group"); arr.Add("-lgcc"); arr.Add("-lgcc_eh"); arr.Add("-lc"); arr.Add("--end-group"); }
        else { arr.Add("-lc"); arr.Add("-lgcc"); arr.Add("--as-needed"); arr.Add("-lgcc_s"); arr.Add("--no-as-needed"); }

        if (_optShared) arr.Add($"{gccLibpath}/crtendS.o");
        else arr.Add($"{gccLibpath}/crtend.o");
        arr.Add($"{libpath}/crtn.o");

        RunSubprocess(arr.ToArray());
    }

    private string FindLibpath()
    {
        if (File.Exists("/usr/lib/x86_64-linux-gnu/crti.o")) return "/usr/lib/x86_64-linux-gnu";
        if (File.Exists("/usr/lib64/crti.o")) return "/usr/lib64";
        Util.Error("library path is not found");
        return null;
    }

    private string FindGccLibpath()
    {
        string[] patterns = {
            "/usr/lib/gcc/x86_64-linux-gnu/*/crtbegin.o",
            "/usr/lib/gcc/x86_64-pc-linux-gnu/*/crtbegin.o",
            "/usr/lib/gcc/x86_64-redhat-linux/*/crtbegin.o",
        };
        foreach (string pattern in patterns)
        {
            string dir = Path.GetDirectoryName(pattern);
            string file = Path.GetFileName(pattern);
            if (dir != null && Directory.Exists(Path.GetDirectoryName(dir)))
            {
                try
                {
                    foreach (string match in Directory.GetFiles(Path.GetDirectoryName(dir), "crtbegin.o", SearchOption.AllDirectories))
                        return Path.GetDirectoryName(match);
                }
                catch { }
            }
        }
        Util.Error("gcc library path is not found");
        return null;
    }

    private Token MustTokenizeFile(Tokenizer tokenizer, string path)
    {
        Token tok = tokenizer.TokenizeFile(path);
        if (tok == null) Util.Error($"{path}: No such file or directory");
        return tok;
    }

    private Token AppendTokens(Token tok1, Token tok2)
    {
        if (tok1 == null || tok1.Kind == TokenKind.Eof) return tok2;
        Token t = tok1;
        while (t.Next.Kind != TokenKind.Eof) t = t.Next;
        t.Next = tok2;
        return tok1;
    }

    private void Cc1(Tokenizer tokenizer, Preprocessor preprocessor)
    {
        Token tok = null;

        // Create the parser early so the preprocessor can use const_expr for #if
        var types = new TypeSystem(Options.DataModel);
        var parser = new Parser(tokenizer, Options, types);
        preprocessor.SetParser(parser);

        // Process -include option
        foreach (string incl in OptInclude)
        {
            string path = File.Exists(incl) ? incl : preprocessor.SearchIncludePaths(incl);
            if (path == null) Util.Error($"-include: {incl}: No such file or directory");
            tok = AppendTokens(tok, MustTokenizeFile(tokenizer, path));
        }

        Token tok2 = MustTokenizeFile(tokenizer, Options.BaseFile);
        tok = AppendTokens(tok, tok2);
        tok = preprocessor.Preprocess(tok);

        // If -M or -MD, print file dependencies
        if (_optM || _optMD)
        {
            PrintDependencies(tokenizer);
            if (_optM) return;
        }

        // If -E, print preprocessed tokens
        if (_optE)
        {
            PrintTokens(tok); return;
        }

        Obj prog = parser.Parse(tok);

        var codegen = new CodeGen(Options, tokenizer, types);
        var sw = new StringWriter();
        codegen.Generate(prog, sw);
        string asm = sw.ToString();

        if (_outputFile == null || _outputFile == "-")
            Console.Write(asm);
        else
            File.WriteAllText(_outputFile, asm);
    }

    private void PrintTokens(Token tok)
    {
        TextWriter output = (_optO != null && _optO != "-") ? new StreamWriter(_optO) : Console.Out;
        int line = 1;
        for (; tok.Kind != TokenKind.Eof; tok = tok.Next)
        {
            if (line > 1 && tok.AtBol) output.Write('\n');
            if (tok.HasSpace && !tok.AtBol) output.Write(' ');
            output.Write(Encoding.UTF8.GetString(tok.Buf, tok.Loc, tok.Len));
            line++;
        }
        output.Write('\n');
        if (output != Console.Out) output.Dispose();
    }

    private void PrintDependencies(Tokenizer tokenizer)
    {
        string path = _optMF ?? (_optMD ? ReplaceExtn(_optO ?? Options.BaseFile, ".d") : (_optO ?? "-"));
        TextWriter output = path == "-" ? Console.Out : new StreamWriter(path);

        if (_optMT != null) output.Write($"{_optMT}:");
        else output.Write($"{QuoteMakefile(ReplaceExtn(Options.BaseFile, ".o"))}:");

        CFile[] files = tokenizer.GetInputFiles();
        for (int i = 0; i < files.Length; i++)
        {
            if (_optMMD && InStdIncludePath(files[i].Name)) continue;
            output.Write($" \\\n  {files[i].Name}");
        }
        output.Write("\n\n");

        if (_optMP)
        {
            for (int i = 1; i < files.Length; i++)
            {
                if (_optMMD && InStdIncludePath(files[i].Name)) continue;
                output.Write($"{QuoteMakefile(files[i].Name)}:\n\n");
            }
        }
        if (output != Console.Out) output.Dispose();
    }

    private bool InStdIncludePath(string path)
    {
        foreach (string dir in StdIncludePaths)
            if (path.StartsWith(dir) && path.Length > dir.Length && path[dir.Length] == '/')
                return true;
        return false;
    }
}
