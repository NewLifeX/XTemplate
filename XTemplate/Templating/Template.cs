﻿using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CSharp;
using NewLife;
using NewLife.Reflection;

namespace XTemplate.Templating
{
    /// <summary>模版引擎</summary>
    /// <remarks>
    /// 模版引擎分为快速用法和增强用法两种，其中增强用法可用于对模版处理的全程进行干预。
    /// 一个模版引擎实例，可用重复使用以处理多个模版。
    /// </remarks>
    /// <example>
    /// 快速用法（单个模版处理）：
    /// <code>
    /// // 以名值字典的方式传入参数，模版内部通过Data["name"]的方式获取，也可以使用GetData&lt;T&gt;(String name)方法
    /// Dictionary&lt;String, Object&gt; data = new Dictionary&lt;String, Object&gt;();
    /// data["name"] = "参数测试";
    /// 
    /// // 如果有包含模版，则以相对于模版文件的路径加载
    /// String content = Template.ProcessFile("模版文件.txt", data);
    /// // 传入内容的用法。如果不指定模版名，则默认使用Class。如果有包含模版，则无法识别。
    /// // String content = Template.ProcessTemplate("模版名", "模版内容", data);
    /// </code>
    /// 中级用法（多模版处理推荐）：
    /// <code>
    /// // 多个模版，包括被包含的模版，一起打包成为模版集合。如果有包含模版，则根据模版名来引用
    /// Dictionary&lt;String, String&gt; templates = new Dictionary&lt;String, String&gt;();
    /// templates.Add("模版1"， File.ReadAllText("模版文件1.txt"));
    /// templates.Add("模版2"， File.ReadAllText("模版文件2.txt"));
    /// templates.Add("模版3"， File.ReadAllText("模版文件3.txt"));
    /// 
    /// Dictionary&lt;String, Object&gt; data = new Dictionary&lt;String, Object&gt;();
    /// data["name"] = "参数测试";
    /// 
    /// Template tt = Create(name, template);
    /// String content = tt.Render("模版1", data);
    /// </code>
    /// 高级用法（仅用于需要仔细控制每一步的场合，比如仅编译模版来检查语法）：
    /// <code>
    /// Template tt = new Template();
    /// tt.AddTemplateItem("模版1"， File.ReadAllText("模版文件1.txt"));
    /// tt.AddTemplateItem("模版2"， File.ReadAllText("模版文件2.txt"));
    /// tt.AddTemplateItem("模版3"， File.ReadAllText("模版文件3.txt"));
    /// tt.Process();
    /// tt.Compile();
    /// TemplateBase temp = tt.CreateInstance("模版1");
    /// temp.Data["name"] = "参数测试";
    /// return temp.Render();
    /// </code>
    /// </example>
    public partial class Template : DisposeBase
    {
        #region 属性
        /// <summary>编译错误集合</summary>
        public CompilerErrorCollection Errors { get; private set; } = new CompilerErrorCollection();

        /// <summary>模版集合</summary>
        public List<TemplateItem> Templates { get; private set; } = new List<TemplateItem>();

        /// <summary>程序集引用</summary>
        public List<String> AssemblyReferences { get; private set; } = new List<String>();

        /// <summary>程序集名称。一旦指定，编译时将会生成持久化的模版程序集文件。</summary>
        public String AssemblyName { get; set; }

        private Assembly _Assembly;
        /// <summary>程序集</summary>
        public Assembly Assembly
        {
            get
            {
                if (_Assembly == null && !String.IsNullOrEmpty(AssemblyName))
                {
                    var file = AssemblyName;
                    if (!Path.IsPathRooted(file)) file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, file);
                    if (!File.Exists(file)) file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Path.Combine("Bin", AssemblyName));
                    if (File.Exists(file)) _Assembly = Assembly.LoadFile(file);
                }
                return _Assembly;
            }
            private set => _Assembly = value;
        }

        private CodeDomProvider _Provider;
        /// <summary>代码生成提供者</summary>
        private CodeDomProvider Provider
        {
            get => _Provider ??= new CSharpCodeProvider();
            set => _Provider = value;
        }

        private String _NameSpace;
        /// <summary>命名空间</summary>
        public String NameSpace
        {
            get
            {
                if (_NameSpace == null)
                {
                    if (!String.IsNullOrEmpty(AssemblyName))
                    {
                        var name = Path.GetFileNameWithoutExtension(AssemblyName);
                        _NameSpace = name;
                    }
                    else
                    {
                        var namespaceName = GetType().Namespace;
                        namespaceName += "s";

                        _NameSpace = namespaceName;
                    }
                }
                return _NameSpace;
            }
            set => _NameSpace = value;
        }

        /// <summary>模版引擎状态</summary>
        public TemplateStatus Status { get; set; }
        #endregion

        #region 创建
        private static readonly ConcurrentDictionary<String, Template> cache = new ConcurrentDictionary<String, Template>();
        /// <summary>根据名称和模版创建模版实例，带缓存，避免重复编译</summary>
        /// <param name="name">名称</param>
        /// <param name="templates">模版</param>
        /// <returns></returns>
        public static Template Create(String name, params String[] templates)
        {
            if (templates == null || templates.Length < 1) throw new ArgumentNullException("templates");

            var dic = new Dictionary<String, String>();

            var prefix = !String.IsNullOrEmpty(name) ? name : "Class";

            if (templates.Length == 1)
            {
                dic.Add(prefix, templates[0]);
            }
            else
            {
                for (var i = 0; i < templates.Length; i++)
                {
                    dic.Add(prefix + (i + 1), templates[i]);
                }
            }

            return Create(dic);
        }

        /// <summary>根据名称和模版创建模版实例，带缓存，避免重复编译</summary>
        /// <param name="templates">模版集合</param>
        /// <returns></returns>
        public static Template Create(IDictionary<String, String> templates)
        {
            if (templates == null || templates.Count < 1) throw new ArgumentNullException("templates");

            // 计算hash
            var sb = new StringBuilder();
            foreach (var item in templates)
            {
                sb.Append(Hash(item.Key));
                sb.Append(Hash(item.Value));
            }

            var hash = Hash(sb.ToString());
            return cache.GetOrAdd(hash, k =>
            {
                var entity = new Template();

                foreach (var item in templates)
                {
                    entity.AddTemplateItem(item.Key, item.Value);
                }

                //entity.Process();
                //entity.Compile();
                return entity;
            });
        }

        /// <summary>销毁</summary>
        /// <param name="disposing"></param>
        protected override void Dispose(Boolean disposing)
        {
            base.Dispose(disposing);

            _Provider.TryDispose();
            _Provider = null;
        }
        #endregion

        #region 快速处理
        /// <summary>通过指定模版文件和传入模版的参数处理模版，返回结果</summary>
        /// <remarks>
        /// 该方法是处理模版的快速方法，把分析、编译和运行三步集中在一起。
        /// 带有缓存，避免重复分析模版。尽量以模版内容为key，防止模版内容改变后没有生效。
        /// </remarks>
        /// <example>
        /// 快速用法：
        /// <code>
        /// Dictionary&lt;String, Object&gt; data = new Dictionary&lt;String, Object&gt;();
        /// data["name"] = "参数测试";
        /// String content = Template.ProcessFile("模版文件.txt", data);
        /// </code>
        /// </example>
        /// <param name="templateFile">模版文件</param>
        /// <param name="data">传入模版的参数，模版中可以使用Data[名称]获取</param>
        /// <returns></returns>
        public static String ProcessFile(String templateFile, IDictionary<String, Object> data)
        {
            // 尽量以模版内容为key，防止模版内容改变后没有生效
            var template = File.ReadAllText(templateFile);

            return ProcessTemplate(templateFile, template, data);
        }

        /// <summary>通过指定模版内容和传入模版的参数处理模版，返回结果</summary>
        /// <example>
        /// 快速用法：
        /// <code>
        /// Dictionary&lt;String, Object&gt; data = new Dictionary&lt;String, Object&gt;();
        /// data["name"] = "参数测试";
        /// String content = Template.ProcessTemplate("模版内容", data);
        /// </code>
        /// </example>
        /// <param name="template">模版内容</param>
        /// <param name="data">模版参数</param>
        /// <returns></returns>
        public static String ProcessTemplate(String template, IDictionary<String, Object> data) => ProcessTemplate(null, template, data);

        /// <summary>通过指定模版内容和传入模版的参数处理模版，返回结果</summary>
        /// <param name="name">模版名字</param>
        /// <param name="template">模版内容</param>
        /// <param name="data">模版参数</param>
        /// <returns></returns>
        public static String ProcessTemplate(String name, String template, IDictionary<String, Object> data)
        {
            if (String.IsNullOrEmpty(template)) throw new ArgumentNullException("template");

            var tt = Create(name, template);

            return tt.Render(tt.Templates[0].ClassName, data);
        }
        #endregion

        #region 分析模版
        /// <summary>处理预先写入Templates的模版集合，模版生成类的代码在Sources中返回</summary>
        public void Process()
        {
            if (Templates == null || Templates.Count < 1) throw new InvalidOperationException("在Templates中未找到待处理模版！");

            if (Status >= TemplateStatus.Process) return;

            for (var i = 0; i < Templates.Count; i++)
            {
                Process(Templates[i]);
            }

            Status = TemplateStatus.Process;
        }

        /// <summary>
        /// 添加模版项，实际上是添加到Templates集合中。
        /// 未指定模版名称时，使用模版的散列作为模版名称
        /// </summary>
        /// <param name="name">名称</param>
        /// <param name="content"></param>
        public void AddTemplateItem(String name, String content)
        {
            if (String.IsNullOrEmpty(name) && String.IsNullOrEmpty(content))
                throw new ArgumentNullException("content", "名称和模版内容不能同时为空！");

            if (Status >= TemplateStatus.Process) throw new InvalidOperationException("模版已分析处理，不能再添加模版！");

            // 未指定模版名称时，使用模版的散列作为模版名称
            if (String.IsNullOrEmpty(name)) name = Hash(content);

            var item = FindTemplateItem(name);
            if (item == null)
            {
                item = new TemplateItem();
                Templates.Add(item);
            }
            item.Name = name;
            item.Content = content;

            // 设置类名
            var cname = Path.GetFileNameWithoutExtension(name);
            // 如果无扩展的名称跟前面的名称不同，并且无扩展名称跟编码后的类名相同，则设置类型为无扩展名称
            if (cname != name && cname == GetClassName(cname))
            {
                // 如果没有别的模版项用这个类名，这里使用
                if (!Templates.Any(t => t.ClassName == cname)) item.ClassName = cname;
            }

            // 设置程序集名，采用最后一级目录名
            if (String.IsNullOrEmpty(AssemblyName))
            {
                var dname = Path.GetDirectoryName(name);
                if (!String.IsNullOrEmpty(dname))
                {
                    dname = Path.GetFileName(dname);
                    if (!String.IsNullOrEmpty(dname)) AssemblyName = dname;
                }
            }
        }

        /// <summary>查找指定名称的模版</summary>
        /// <param name="name">名称</param>
        /// <returns></returns>
        private TemplateItem FindTemplateItem(String name)
        {
            if (Templates == null || Templates.Count < 1) return null;

            foreach (var item in Templates)
            {
                if (item.Name.EqualIgnoreCase(name)) return item;
            }

            // 再根据类名找
            foreach (var item in Templates)
            {
                if (item.ClassName.EqualIgnoreCase(name)) return item;
            }

            return null;
        }

        /// <summary>处理指定模版</summary>
        /// <param name="item">模版项</param>
        private void Process(TemplateItem item)
        {
            // 拆分成块
            item.Blocks = TemplateParser.Parse(item.Name, item.Content);

            // 处理指令
            ProcessDirectives(item);
            //TemplateParser.StripExtraNewlines(item.Blocks);

            if (Imports != null) item.Imports.AddRange(Imports);
            //if (References != null) context.References.AddRange(References);

            // 生成代码
            item.Source = ConstructGeneratorCode(item, Debug, NameSpace, Provider);
        }
        #endregion

        #region 处理指令
        /// <summary>处理指令</summary>
        /// <param name="item"></param>
        /// <returns>返回指令集合</returns>
        private void ProcessDirectives(TemplateItem item)
        {
            // 使用包含堆栈处理包含，检测循环包含
            var includeStack = new Stack<String>();
            includeStack.Push(item.Name);

            var directives = new String[] { "template", "assembly", "import", "include", "var" };

            for (var i = 0; i < item.Blocks.Count; i++)
            {
                var block = item.Blocks[i];
                if (block.Type != BlockType.Directive) continue;

                // 弹出当前块的模版名
                while (includeStack.Count > 0 && !includeStack.Peek().EqualIgnoreCase(block.Name))
                {
                    includeStack.Pop();
                }
                var directive = TemplateParser.ParseDirectiveBlock(block);
                if (directive == null || Array.IndexOf(directives, directive.Name.ToLower()) < 0)
                    throw new TemplateException(block, String.Format("无法识别的指令：{0}！", block.Text));

                // 包含指令时，返回多个代码块
                //List<Block> list = ProcessDirective(directive, item);
                var ti = ProcessDirective(directive, item);
                if (ti == null) continue;
                //List<Block> list = TemplateParser.Parse(ti.Name, ti.Content);
                // 拆分成块
                if (ti.Blocks == null || ti.Blocks.Count < 1) ti.Blocks = TemplateParser.Parse(ti.Name, ti.Content);
                if (ti.Blocks == null || ti.Blocks.Count < 1) continue;

                var list = ti.Blocks;
                var name = ti.Name;
                if (includeStack.Contains(name)) throw new TemplateException(block, String.Format("循环包含名为[{0}]的模版！", name));

                includeStack.Push(name);

                // 包含模版并入当前模版
                item.Blocks.InsertRange(i + 1, list);
            }
        }

        private TemplateItem ProcessDirective(Directive directive, TemplateItem item)
        {
            #region 包含include
            if (directive.Name.EqualIgnoreCase("include"))
            {
                var name = directive.GetParameter("name");
                // 可能采用了相对路径
                if (!File.Exists(name)) name = Path.Combine(Path.GetDirectoryName(item.Name), name);

                //String content = null;
                var ti = FindTemplateItem(name);
                if (ti != null)
                {
                    ti.Included = true;
                    //content = ti.Content;
                }
                else
                {
                    // 尝试读取文件
                    if (File.Exists(name))
                    {
                        ti = new TemplateItem
                        {
                            Name = name,
                            Content = File.ReadAllText(name)
                        };
                        Templates.Add(ti);

                        ti.Included = true;
                        //content = ti.Content;
                    }
                }
                // 允许内容为空
                //if (String.IsNullOrEmpty(content)) throw new TemplateException(directive.Block, String.Format("加载模版[{0}]失败！", name));

                return ti;
            }
            #endregion

            if (directive.Name.EqualIgnoreCase("assembly"))
            {
                var name = directive.GetParameter("name");
                if (!AssemblyReferences.Contains(name)) AssemblyReferences.Add(name);
            }
            else if (directive.Name.EqualIgnoreCase("import"))
            {
                item.Imports.Add(directive.GetParameter("namespace"));
            }
            else if (directive.Name.EqualIgnoreCase("template"))
            {
                if (item.Processed) throw new TemplateException(directive.Block, "多个模版指令！");

                // 由模版指令指定类名
                if (directive.TryGetParameter("name", out var name) && !String.IsNullOrEmpty(name)) item.ClassName = name;

                //item.BaseClassName = directive.GetParameter("inherits");
                if (directive.TryGetParameter("inherits", out name)) item.BaseClassName = name;
                item.Processed = true;

            }
            else if (directive.Name.EqualIgnoreCase("var"))
            {
                var name = directive.GetParameter("name");
                var type = directive.GetParameter("type");

                if (item.Vars.ContainsKey(name)) throw new TemplateException(directive.Block, "模版变量" + name + "已存在！");

                var ptype = type.GetTypeEx();
                if (ptype == null) throw new TemplateException(directive.Block, "无法找到模版变量类型" + type + "！");

                // 因为TypeX.GetType的强大，模版可能没有引用程序集和命名空间，甚至type位于未装载的程序集中它也会自动装载，所以这里需要加上
                ImportType(item, ptype);
                item.Vars.Add(name, ptype);
            }
            return null;
        }

        /// <summary>导入某类型，导入程序集引用及命名空间引用，主要处理泛型</summary>
        /// <param name="item"></param>
        /// <param name="type">类型</param>
        private void ImportType(TemplateItem item, Type type)
        {
            String name = null;
            try
            {
                name = type.Assembly.Location;
            }
            catch { }

            if (!String.IsNullOrEmpty(name) && !AssemblyReferences.Contains(name)) AssemblyReferences.Add(name);
            name = type.Namespace;
            if (!item.Imports.Contains(name)) item.Imports.Add(name);

            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                foreach (var elm in type.GetGenericArguments())
                {
                    if (!elm.IsGenericParameter) ImportType(item, elm);
                }
            }
        }
        #endregion

        #region 生成代码
        private static String ConstructGeneratorCode(TemplateItem ti, Boolean lineNumbers, String namespaceName, CodeDomProvider provider)
        {
            // 准备类名和命名空间
            var codeNamespace = new CodeNamespace(namespaceName);

            // 加入引用的命名空间
            foreach (var str in ti.Imports)
            {
                if (!String.IsNullOrEmpty(str)) codeNamespace.Imports.Add(new CodeNamespaceImport(str));
            }
            var typeDec = new CodeTypeDeclaration(ti.ClassName)
            {
                IsClass = true
            };
            codeNamespace.Types.Add(typeDec);

            // 基类
            if (!String.IsNullOrEmpty(ti.BaseClassName))
                typeDec.BaseTypes.Add(new CodeTypeReference(ti.BaseClassName));
            else if (!String.IsNullOrEmpty(BaseClassName))
                typeDec.BaseTypes.Add(new CodeTypeReference(BaseClassName));
            else
                typeDec.BaseTypes.Add(new CodeTypeReference(typeof(TemplateBase)));

            if (lineNumbers && !String.IsNullOrEmpty(ti.Name)) typeDec.LinePragma = new CodeLinePragma(ti.Name, 1);

            // Render方法
            CreateRenderMethod(ti.Blocks, lineNumbers, typeDec);

            // 代码生成选项
            var options = new CodeGeneratorOptions
            {
                VerbatimOrder = true,
                BlankLinesBetweenMembers = false,
                BracingStyle = "C"
            };

            // 其它类成员代码块
            var firstMemberFound = false;
            foreach (var block in ti.Blocks)
            {
                firstMemberFound = GenerateMemberForBlock(block, typeDec, lineNumbers, provider, options, firstMemberFound);
            }

            // 模版变量
            if (ti.Vars != null && ti.Vars.Count > 0)
            {
                // 构建静态构造函数，初始化静态属性Vars
                CreateCctorMethod(typeDec, ti.Vars);

                //public Int32 VarName
                //{
                //    get { return (Int32)GetData("VarName"); }
                //    set { Data["VarName"] = value; }
                //}
                foreach (var v in ti.Vars)
                {
                    //var vtype = TypeX.Create(v.Value);
                    var codeName = v.Value.GetName(true);

                    var sb = new StringBuilder();
                    sb.AppendLine();
                    sb.AppendFormat("public {0} {1}", codeName, v.Key);
                    sb.AppendLine("{");
                    sb.AppendFormat("    get {{ return GetData<{0}>(\"{1}\"); }}", codeName, v.Key);
                    sb.AppendLine();
                    sb.AppendFormat("    set {{ Data[\"{0}\"] = value; }}", v.Key);
                    sb.AppendLine();
                    sb.AppendLine("}");

                    typeDec.Members.Add(new CodeSnippetTypeMember(sb.ToString()));
                }
            }

            // 输出
            using var writer = new StringWriter();
            provider.GenerateCodeFromNamespace(codeNamespace, new IndentedTextWriter(writer), options);
            return writer.ToString();
        }

        /// <summary>生成Render方法</summary>
        /// <param name="blocks"></param>
        /// <param name="lineNumbers"></param>
        /// <param name="typeDec"></param>
        private static void CreateRenderMethod(List<Block> blocks, Boolean lineNumbers, CodeTypeDeclaration typeDec)
        {
            var method = new CodeMemberMethod();
            typeDec.Members.Add(method);
            method.Name = "Render";
            method.Attributes = MemberAttributes.Override | MemberAttributes.Public;
            method.ReturnType = new CodeTypeReference(typeof(String));

            // 生成代码
            var statementsMain = method.Statements;
            var firstMemberFound = false;
            foreach (var block in blocks)
            {
                if (block.Type == BlockType.Directive) continue;
                if (block.Type == BlockType.Member)
                {
                    // 遇到类成员代码块，标识取反
                    firstMemberFound = !firstMemberFound;
                    continue;
                }
                // 只要现在还在类成员代码块区域内，就不做处理
                if (firstMemberFound) continue;

                if (block.Type == BlockType.Statement)
                {
                    // 代码语句，直接拼接
                    var statement = new CodeSnippetStatement(block.Text);
                    if (lineNumbers)
                        AddStatementWithLinePragma(block, statementsMain, statement);
                    else
                        statementsMain.Add(statement);
                }
                else if (block.Type == BlockType.Text)
                {
                    // 模版文本，直接Write
                    if (!String.IsNullOrEmpty(block.Text))
                    {
                        var exp = new CodeMethodInvokeExpression(new CodeThisReferenceExpression(), "Write", new CodeExpression[] { new CodePrimitiveExpression(block.Text) });
                        //statementsMain.Add(exp);
                        var statement = new CodeExpressionStatement(exp);
                        if (lineNumbers)
                            AddStatementWithLinePragma(block, statementsMain, statement);
                        else
                            statementsMain.Add(statement);
                    }
                }
                else
                {
                    // 表达式，直接Write
                    var exp = new CodeMethodInvokeExpression(new CodeThisReferenceExpression(), "Write", new CodeExpression[] { new CodeArgumentReferenceExpression(block.Text.Trim()) });
                    var statement = new CodeExpressionStatement(exp);
                    if (lineNumbers)
                        AddStatementWithLinePragma(block, statementsMain, statement);
                    else
                        statementsMain.Add(statement);
                }
            }

            statementsMain.Add(new CodeMethodReturnStatement(new CodeMethodInvokeExpression(new CodeMethodReferenceExpression(new CodePropertyReferenceExpression(new CodeThisReferenceExpression(), "Output"), "ToString"), new CodeExpression[0])));
        }

        private static void CreateCctorMethod(CodeTypeDeclaration typeDec, IDictionary<String, Type> vars)
        {
            //CodeTypeConstructor method = new CodeTypeConstructor();
            var method = new CodeConstructor();
            typeDec.Members.Add(method);
            method.Attributes = MemberAttributes.Public;

            // 生成代码
            var statementsMain = method.Statements;
            // vars.Add(item, vars[item]);
            foreach (var item in vars)
            {
                var methodRef = new CodeMethodReferenceExpression(new CodePropertyReferenceExpression(null, "Vars"), "Add");
                statementsMain.Add(new CodeMethodInvokeExpression(methodRef, new CodePrimitiveExpression(item.Key), new CodeTypeOfExpression(item.Value)));
            }
        }

        private static void AddStatementWithLinePragma(Block block, CodeStatementCollection statements, CodeStatement statement)
        {
            var lineNumber = (block.StartLine > 0) ? block.StartLine : 1;

            if (String.IsNullOrEmpty(block.Name))
                statements.Add(new CodeSnippetStatement("#line " + lineNumber));
            else
                statement.LinePragma = new CodeLinePragma(block.Name, lineNumber);
            statements.Add(statement);
            if (String.IsNullOrEmpty(block.Name)) statements.Add(new CodeSnippetStatement("#line default"));
        }

        /// <summary>生成成员代码块</summary>
        /// <param name="block"></param>
        /// <param name="generatorType"></param>
        /// <param name="lineNumbers"></param>
        /// <param name="provider"></param>
        /// <param name="options"></param>
        /// <param name="firstMemberFound"></param>
        /// <returns></returns>
        private static Boolean GenerateMemberForBlock(Block block, CodeTypeDeclaration generatorType, Boolean lineNumbers, CodeDomProvider provider, CodeGeneratorOptions options, Boolean firstMemberFound)
        {
            CodeSnippetTypeMember member = null;
            if (!firstMemberFound)
            {
                // 发现第一个<#!后，认为是类成员代码的开始，直到下一个<#!作为结束
                if (block.Type == BlockType.Member)
                {
                    firstMemberFound = true;
                    if (!String.IsNullOrEmpty(block.Text)) member = new CodeSnippetTypeMember(block.Text);
                }
            }
            else
            {
                // 再次遇到<#!，此时，成员代码准备结束
                if (block.Type == BlockType.Member)
                {
                    firstMemberFound = false;
                    if (!String.IsNullOrEmpty(block.Text)) member = new CodeSnippetTypeMember(block.Text);
                }
                else if (block.Type == BlockType.Text)
                {
                    var expression = new CodeMethodInvokeExpression(new CodeThisReferenceExpression(), "Write", new CodeExpression[] { new CodePrimitiveExpression(block.Text) });
                    var statement = new CodeExpressionStatement(expression);
                    using var writer = new StringWriter();
                    provider.GenerateCodeFromStatement(statement, writer, options);
                    member = new CodeSnippetTypeMember(writer.ToString());
                }
                else if (block.Type == BlockType.Expression)
                {
                    var expression = new CodeMethodInvokeExpression(new CodeThisReferenceExpression(), "Write", new CodeExpression[] { new CodeArgumentReferenceExpression(block.Text.Trim()) });
                    var statement = new CodeExpressionStatement(expression);
                    using var writer = new StringWriter();
                    provider.GenerateCodeFromStatement(statement, writer, options);
                    member = new CodeSnippetTypeMember(writer.ToString());
                }
                else if (block.Type == BlockType.Statement)
                {
                    member = new CodeSnippetTypeMember(block.Text);
                }
            }
            if (member != null)
            {
                if (lineNumbers)
                {
                    var flag = String.IsNullOrEmpty(block.Name);
                    var lineNumber = (block.StartLine > 0) ? block.StartLine : 1;
                    if (flag)
                        generatorType.Members.Add(new CodeSnippetTypeMember("#line " + lineNumber));
                    else
                        member.LinePragma = new CodeLinePragma(block.Name, lineNumber);
                    generatorType.Members.Add(member);
                    if (flag) generatorType.Members.Add(new CodeSnippetTypeMember("#line default"));
                }
                else
                    generatorType.Members.Add(member);
            }
            return firstMemberFound;
        }
        #endregion

        #region 编译模版
        /// <summary>编译运行</summary>
        /// <returns></returns>
        public Assembly Compile()
        {
            if (Status >= TemplateStatus.Compile) return Assembly;

            if (Status < TemplateStatus.Process) Process();

            if (References != null) AssemblyReferences.AddRange(References);

            var name = AssemblyName;
            if (String.IsNullOrEmpty(Path.GetExtension(name))) name += ".dll";
            var asm = Compile(name, AssemblyReferences, Provider, Errors, this);
            if (asm != null) Assembly = asm;

            // 释放提供者
            Provider = null;

            Status = TemplateStatus.Compile;

            return asm;
        }

        private static readonly Dictionary<String, Assembly> asmCache = new Dictionary<String, Assembly>();
        private static Assembly Compile(String outputAssembly, IEnumerable<String> references, CodeDomProvider provider, CompilerErrorCollection errors, Template tmp)
        {
            //String key = outputAssembly;
            //if (String.IsNullOrEmpty(key)) key = Hash(String.Join(Environment.NewLine, sources));

            var sb = new StringBuilder();
            foreach (var item in tmp.Templates)
            {
                if (item.Included) continue;

                if (sb.Length > 0) sb.AppendLine();
                sb.Append(item.Source);
            }
            var key = Hash(sb.ToString());

            if (asmCache.TryGetValue(key, out var assembly)) return assembly;
            lock (asmCache)
            {
                if (asmCache.TryGetValue(key, out assembly)) return assembly;
                foreach (var str in references)
                {
                    try
                    {
                        if (!String.IsNullOrEmpty(str) && File.Exists(str)) Assembly.LoadFrom(str);
                    }
                    catch { }
                }

                assembly = CompileInternal(outputAssembly, references, provider, errors, tmp);
                if (assembly != null) asmCache.Add(key, assembly);

                return assembly;
            }
        }

        private static Assembly CompileInternal(String outputAssembly, IEnumerable<String> references, CodeDomProvider provider, CompilerErrorCollection Errors, Template tmp)
        {
            var options = new CompilerParameters();
            foreach (var str in references)
            {
                options.ReferencedAssemblies.Add(str);
            }
            options.WarningLevel = 4;

            CompilerResults results = null;
            if (Debug)
            {
                //var sb = new StringBuilder();

                #region 调试状态，把生成的类文件和最终dll输出到XTemp目录下
                var tempPath = NewLife.Setting.Current.DataPath.GetFullPath().EnsureDirectory(false);
                //if (!String.IsNullOrEmpty(outputAssembly)) tempPath = Path.Combine(tempPath, Path.GetFileNameWithoutExtension(outputAssembly));
                if (!String.IsNullOrEmpty(outputAssembly) && !outputAssembly.EqualIgnoreCase(".dll")) tempPath = Path.Combine(tempPath, Path.GetFileNameWithoutExtension(outputAssembly));

                //if (!String.IsNullOrEmpty(tempPath) && !Directory.Exists(tempPath)) Directory.CreateDirectory(tempPath);

                //var srcpath = tempPath.CombinePath("src").EnsureDirectory(false);

                var files = new List<String>();
                foreach (var item in tmp.Templates)
                {
                    // 输出模版内容，为了调试使用
                    File.WriteAllText(tempPath.CombinePath(item.Name).EnsureDirectory(true), item.Content);
                    if (item.Included) continue;

                    var name = item.Name.EndsWithIgnoreCase(".cs") ? item.Name : item.ClassName;
                    // 猜测后缀
                    var p = name.LastIndexOf("_");
                    if (p > 0 && name.Length - p <= 5)
                        name = name.Substring(0, p) + "." + name.Substring(p + 1, name.Length - p - 1);
                    else if (!name.EndsWithIgnoreCase(".cs"))
                        name += ".cs";

                    //name = Path.Combine(tempPath, name);
                    //name = srcpath.CombinePath(name);
                    // 必须放在同一个目录，编译时会搜索源码所在目录
                    name = tempPath.CombinePath(Path.GetFileNameWithoutExtension(name) + "_src" + Path.GetExtension(name));
                    File.WriteAllText(name, item.Source);

                    //sb.AppendLine(item.Source);
                    files.Add(name);
                }
                #endregion

                if (!String.IsNullOrEmpty(outputAssembly) && !outputAssembly.EqualIgnoreCase(".dll"))
                {
                    options.TempFiles = new TempFileCollection(tempPath, false);
                    options.OutputAssembly = Path.Combine(tempPath, outputAssembly);
                    //options.GenerateInMemory = true;
                    options.IncludeDebugInformation = true;
                }

                results = provider.CompileAssemblyFromFile(options, files.ToArray());
                // 必须从内存字符串编译，否则pdb会定向到最终源代码文件
                //results = provider.CompileAssemblyFromSource(options, new String[] { sb.ToString() });
            }
            else
            {
                options.GenerateInMemory = true;

                results = provider.CompileAssemblyFromSource(options, tmp.Templates.Where(e => !e.Included).Select(e => e.Source).ToArray());
            }

            #region 编译错误处理
            if (results.Errors.Count > 0)
            {
                Errors.AddRange(results.Errors);

                var sb = new StringBuilder();
                CompilerError err = null;
                foreach (CompilerError error in results.Errors)
                {
                    error.ErrorText = error.ErrorText;
                    //if (String.IsNullOrEmpty(error.FileName)) error.FileName = inputFile;

                    if (!error.IsWarning)
                    {
                        var msg = error.ToString();
                        if (sb.Length < 1)
                        {
                            String code = null;
                            // 屏蔽因为计算错误行而导致的二次错误
                            try
                            {
                                code = tmp.FindBlockCode(error.FileName, error.Line);
                            }
                            catch { }
                            if (code != null)
                            {
                                msg += Environment.NewLine;
                                msg += code;
                            }
                            err = error;
                        }
                        else
                            sb.AppendLine();

                        sb.Append(msg);
                    }
                }
                if (sb.Length > 0)
                {
                    var ex = new TemplateException(sb.ToString())
                    {
                        Error = err
                    };
                    throw ex;
                }
            }
            else
            {
                try
                {
                    options.TempFiles.Delete();
                }
                catch { }
            }
            #endregion

            if (!results.Errors.HasErrors)
            {
                try
                {
                    return results.CompiledAssembly;
                }
                catch { }
            }
            return null;
        }

        /// <summary>找到指定文件指定位置上下三行的代码</summary>
        /// <param name="name">名称</param>
        /// <param name="lineNumber"></param>
        /// <returns></returns>
        private String FindBlockCode(String name, Int32 lineNumber)
        {
            if (!String.IsNullOrEmpty(name))
            {
                // 先根据文件名找
                foreach (var item in Templates)
                {
                    if (item.Name != name) continue;

                    var str = FindBlockCodeInItem(name, lineNumber, item);
                    if (!String.IsNullOrEmpty(str)) return str;
                }
                // 然后，模版里面可能包含有模版
                foreach (var item in Templates)
                {
                    if (item.Name == name) continue;

                    var str = FindBlockCodeInItem(name, lineNumber, item);
                    if (!String.IsNullOrEmpty(str)) return str;
                }
            }

            // 第一个符合行号的模版内容，在找不到对应文件的模版时使用
            foreach (var item in Templates)
            {
                // 找到第一个符合行号的模版内容
                if (item.Blocks[item.Blocks.Count - 1].StartLine > lineNumber)
                {
                    var str = FindBlockCodeInItem(null, lineNumber, item);
                    if (!String.IsNullOrEmpty(str)) return str;
                }
            }
            return null;
        }

        private static String FindBlockCodeInItem(String name, Int32 lineNumber, TemplateItem item)
        {
            var nocmpName = String.IsNullOrEmpty(name);
            for (var i = 0; i < item.Blocks.Count; i++)
            {
                var line = item.Blocks[i].StartLine;
                if (line >= lineNumber && (nocmpName || item.Blocks[i].Name == name))
                {
                    // 错误所在段
                    var n = i;
                    if (line > lineNumber)
                    {
                        n--;
                        line = item.Blocks[n].StartLine;
                    }

                    var code = item.Blocks[n].Text;
                    var codeLines = code.Split(new String[] { Environment.NewLine }, StringSplitOptions.None);

                    var sb = new StringBuilder();
                    // 错误行在第一行，需要上一段的最后一行
                    if (n > 0 && line == lineNumber)
                    {
                        var code2 = item.Blocks[n - 1].Text;
                        var codeLines2 = code2.Split(new String[] { Environment.NewLine }, StringSplitOptions.None);
                        sb.AppendLine((lineNumber - 1) + ":" + codeLines2[codeLines2.Length - 1]);
                    }
                    // 错误行代码段
                    {
                        // 错误行不在第一行，需要上一行
                        if (line < lineNumber) sb.AppendLine((lineNumber - 1) + ":" + codeLines[lineNumber - line - 1]);
                        // 错误行
                        sb.AppendLine(lineNumber + ":" + codeLines[lineNumber - line]);
                        // 错误行不在最后一行，需要下一行
                        if (line + codeLines.Length > lineNumber) sb.AppendLine((lineNumber + 1) + ":" + codeLines[lineNumber - line + 1]);
                    }
                    // 错误行在最后一行以后的，需要下一段的第一行
                    if (n < item.Blocks.Count - 1 && line + codeLines.Length <= lineNumber)
                    {
                        var code2 = item.Blocks[n + 1].Text;
                        var codeLines2 = code2.Split(new String[] { Environment.NewLine }, StringSplitOptions.None);
                        sb.AppendLine((lineNumber + 1) + ":" + codeLines2[0]);
                    }
                    return sb.ToString();
                }
            }
            return null;
        }
        #endregion

        #region 运行生成
        /// <summary>创建模版实例</summary>
        /// <param name="className"></param>
        /// <returns></returns>
        public TemplateBase CreateInstance(String className)
        {
            if (Status < TemplateStatus.Compile) Compile();

            if (Assembly == null) throw new InvalidOperationException("尚未编译模版！");

            if (String.IsNullOrEmpty(className))
            {
                var ts = Assembly.GetTypes();
                if (ts != null && ts.Length > 0)
                {
                    // 检查是否只有一个模版类
                    var n = 0;
                    foreach (var type in ts)
                    {
                        if (!type.As<TemplateBase>()) continue;

                        className = type.FullName;
                        if (n++ > 1) break;
                    }

                    //// 干脆用第一个
                    //if (n == 0) className = ts[0].FullName;

                    // 如果只有一个模版类，则使用该类作为类名
                    if (n != 1) throw new ArgumentNullException("className", "请指定模版类类名！");
                }
            }
            else
            {
                var ti = FindTemplateItem(className);
                if (ti != null)
                    className = ti.ClassName;
                else
                    className = GetClassName(className);
            }

            if (!className.Contains(".")) className = NameSpace + "." + className;
            // 可能存在大小写不匹配等问题，这里需要修正
            var temp = Assembly.CreateInstance(className, true) as TemplateBase;
            if (temp == null) throw new Exception(String.Format("没有找到模版类[{0}]！", className));

            temp.Template = this;
            temp.TemplateItem = FindTemplateItem(className);

            return temp;
        }

        /// <summary>运行代码</summary>
        /// <param name="className"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public String Render(String className, IDictionary<String, Object> data)
        {
            // 2012.11.06 尝试共享已加载的程序集
            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(CurrentDomain_AssemblyResolve);

            var temp = CreateInstance(className);
            temp.Data = data;
            temp.Initialize();

            try
            {
                return temp.Render();
            }
            catch (Exception ex)
            {
                throw new TemplateExecutionException("模版执行错误！", ex);
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= new ResolveEventHandler(CurrentDomain_AssemblyResolve);
            }
        }

        /// <summary>对程序集解析失败的处理函数</summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        private static Assembly CurrentDomain_AssemblyResolve(Object sender, ResolveEventArgs args)
        {
            var name = args.Name;
            if (name.IsNullOrWhiteSpace()) return null;
            var p = name.IndexOf(',');
            if (p > 0) name = name.Substring(0, p);

            var asms = AppDomain.CurrentDomain.GetAssemblies();
            if (asms == null || asms.Length < 1) return null;

            foreach (var item in asms)
            {
                try
                {
                    if (item.FullName == args.Name) return Assembly.Load(name);
                }
                catch { }
            }

            return null;
        }
        #endregion
    }
}