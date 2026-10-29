using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PclBinaryPatchLab
{
    /// <summary>
    /// Structural PCL patch prototype.
    /// dnlib is used to identify metadata/IL patterns only; the original PE image is then
    /// edited in-place at dynamically calculated offsets, preserving method layout and max-stack headers.
    /// No version lock, file-size lock, fixed file offset, or expected SHA-256 is used.
    /// </summary>
    internal static class PclPatcher
    {
        private sealed class LoaderPatchTarget
        {
            public MethodDef Method;
            public Instruction Start;
            public Instruction End;
            public FieldDef LoaderField;
            public int FileOffset;
            public int Length;
        }

        private sealed class CardPatchTarget
        {
            public MethodDef Method;
            public Instruction Constant;
            public int FileOffset;
        }

        private sealed class PatchPlan
        {
            public LoaderPatchTarget Loader;
            public CardPatchTarget Card;
        }

        public static string CreatePatchedCopy(string inputPath, string outputPath, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(inputPath)) throw new ArgumentException("输入路径为空。", nameof(inputPath));
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("输出路径为空。", nameof(outputPath));

            inputPath = Path.GetFullPath(inputPath);
            outputPath = Path.GetFullPath(outputPath);
            if (!File.Exists(inputPath)) throw new FileNotFoundException("找不到输入 EXE。", inputPath);
            if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("输入文件和输出文件不能是同一路径。未修改文件。");
            if (File.Exists(outputPath))
                throw new IOException("输出文件已存在。为避免覆盖，已中止：\r\n" + outputPath);

            byte[] original = File.ReadAllBytes(inputPath);
            log("已读取输入文件：" + original.Length.ToString("N0") + " 字节。此版本不按 SHA-256 或文件大小锁定构建。");
            log("正在使用程序集元数据和 IL 结构定位目标……");

            PatchPlan plan;
            using (ModuleDefMD module = ModuleDefMD.Load(inputPath))
            {
                plan = Analyse(module, original, log);
            }

            byte[] patched = (byte[])original.Clone();

            // The disabled loader expression leaves one LoaderTask object on the evaluation stack.
            // Replace its entire byte range with ldsfld <existing LoaderTask field>; NOP padding
            // keeps every later IL offset, branch target and EH boundary at its original location.
            int loaderLength = plan.Loader.Length;
            if (loaderLength < 5)
                throw new InvalidDataException("目标加载器表达式不足 5 字节，无法放入 ldsfld 指令。未生成文件。");

            patched[plan.Loader.FileOffset] = (byte)OpCodes.Ldsfld.Value;
            byte[] fieldToken = BitConverter.GetBytes(plan.Loader.LoaderField.MDToken.Raw);
            Buffer.BlockCopy(fieldToken, 0, patched, plan.Loader.FileOffset + 1, fieldToken.Length);
            for (int i = 5; i < loaderLength; i++)
                patched[plan.Loader.FileOffset + i] = (byte)OpCodes.Nop.Value;

            // Change Visibility.Collapsed (enum value 2) to Visibility.Visible (enum value 0).
            // ldc.i4.2 and ldc.i4.0 are both one-byte IL instructions, so no layout changes occur.
            patched[plan.Card.FileOffset] = (byte)OpCodes.Ldc_I4_0.Value;

            EnsureOnlyApprovedRangesChanged(original, patched,
                plan.Loader.FileOffset, plan.Loader.Length, plan.Card.FileOffset);

            log("补丁 1：" + plan.Loader.Method.FullName + "；文件偏移 0x" + plan.Loader.FileOffset.ToString("X") +
                "；区域长度 " + plan.Loader.Length + " 字节；字段令牌 0x" + plan.Loader.LoaderField.MDToken.Raw.ToString("X8") + "。");
            log("补丁 2：" + plan.Card.Method.FullName + "；文件偏移 0x" + plan.Card.FileOffset.ToString("X") + "。");
            log("结构定位成功。正在写入新文件……");

            using (FileStream stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(patched, 0, patched.Length);
                stream.Flush(true);
            }

            try
            {
                byte[] written = File.ReadAllBytes(outputPath);
                if (written.Length != original.Length)
                    throw new InvalidDataException("输出复检失败：文件大小发生了变化。");
                EnsureOnlyApprovedRangesChanged(original, written,
                    plan.Loader.FileOffset, plan.Loader.Length, plan.Card.FileOffset);

                using (ModuleDefMD verify = ModuleDefMD.Load(outputPath))
                {
                    VerifyPatchedStructure(verify, log);
                }

                log("复检通过：程序集可被重新解析，文件长度不变，只有两个目标区域发生变化。");
                log("提示：请先运行输出副本测试；结构校验通过不等于完整的游戏下载安装流程已人工验证。");
                return outputPath;
            }
            catch
            {
                try { File.Delete(outputPath); } catch { }
                throw;
            }
        }

        private static PatchPlan Analyse(ModuleDef module, byte[] image, Action<string> log)
        {
            TypeDef pageInstall = RequireType(module, "PCL.PageDownloadInstall");
            TypeDef modDownload = RequireType(module, "PCL.ModDownload");
            TypeDef pageModify = RequireType(module, "PCL.PageDownloadModify");

            List<FieldDef> loaderFields = modDownload.Fields
                .Where(f => string.Equals(f.Name.ToString(), "DlClientListLoader", StringComparison.Ordinal))
                .ToList();
            if (loaderFields.Count != 1)
                throw new InvalidDataException("结构不匹配：PCL.ModDownload.DlClientListLoader 字段数量为 " + loaderFields.Count + "，应为 1。未生成文件。");
            FieldDef loaderField = loaderFields[0];
            if (!loaderField.IsStatic || loaderField.FieldType.FullName.IndexOf("LoaderTask", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidDataException("结构不匹配：DlClientListLoader 不是预期的静态加载器字段。未生成文件。");

            List<LoaderPatchTarget> loaderCandidates = FindDisabledLoaderRanges(pageInstall, loaderField, image);
            if (loaderCandidates.Count != 1)
            {
                string candidates = string.Join(Environment.NewLine, loaderCandidates.Select(x => "  " + x.Method.FullName));
                string diagnostic = DescribeLoaderMethods(pageInstall);
                throw new InvalidDataException(
                    "结构不匹配：无法唯一定位下载页的关闭用加载器结构。匹配数=" + loaderCandidates.Count + "。\r\n" +
                    (candidates.Length == 0 ? "没有完整结构匹配。" : candidates) + "\r\n\r\n诊断信息：\r\n" + diagnostic + "\r\n未生成文件。");
            }

            List<CardPatchTarget> cardCandidates = FindMinecraftCardCollapsedPatterns(pageModify, image);
            if (cardCandidates.Count != 1)
                throw new InvalidDataException("结构不匹配：Minecraft 卡片的 Collapsed 可见性模式应唯一，实际匹配数=" + cardCandidates.Count + "。\r\n" +
                    DescribeCardMethods(pageModify) + "\r\n未生成文件。");

            log("定位到下载页方法：" + loaderCandidates[0].Method.FullName);
            log("下载页替换区间：起始 IL 偏移 0x" + loaderCandidates[0].Start.Offset.ToString("X") +
                "，结束锚点 IL 偏移 0x" + loaderCandidates[0].End.Offset.ToString("X") +
                "，长度 " + loaderCandidates[0].Length + " 字节。内部跳转不会被写出器重新布局，因为不重新序列化程序集。 ");
            log("定位到 MC 卡片方法：" + cardCandidates[0].Method.FullName);
            return new PatchPlan { Loader = loaderCandidates[0], Card = cardCandidates[0] };
        }

        private static List<LoaderPatchTarget> FindDisabledLoaderRanges(TypeDef pageInstall, FieldDef loaderField, byte[] image)
        {
            var result = new List<LoaderPatchTarget>();
            foreach (MethodDef method in pageInstall.Methods)
            {
                if (!method.HasBody) continue;
                IList<Instruction> instructions = method.Body.Instructions;
                for (int markerIndex = 0; markerIndex < instructions.Count; markerIndex++)
                {
                    if (!IsDownloadRestrictionMarker(instructions[markerIndex])) continue;

                    // First establish that this is the disabled LoaderTask expression.
                    int constructorIndex = -1;
                    for (int j = markerIndex + 1; j < instructions.Count; j++)
                    {
                        if (instructions[j].OpCode.Code == Code.Newobj && OperandContains(instructions[j], "LoaderTask"))
                        {
                            constructorIndex = j;
                            break;
                        }
                    }
                    if (constructorIndex < 0) continue;

                    // Locate the real continuation call. The byte range must end immediately BEFORE
                    // the retained ldarg.0 that feeds PageLoaderInit, not at the placeholder ctor.
                    // That is important: the disabled path also contains delegate/closure IL after
                    // the ctor, and leaving that tail in place changes stack behaviour.
                    int pageLoaderCallIndex = -1;
                    for (int j = constructorIndex + 1; j < instructions.Count; j++)
                    {
                        Code code = instructions[j].OpCode.Code;
                        if ((code == Code.Call || code == Code.Callvirt || code == Code.Calli) &&
                            OperandContains(instructions[j], "PageLoaderInit"))
                        {
                            pageLoaderCallIndex = j;
                            break;
                        }
                    }
                    if (pageLoaderCallIndex < 0) continue;

                    int anchorIndex = -1;
                    for (int j = pageLoaderCallIndex - 1; j > constructorIndex; j--)
                    {
                        if (instructions[j].OpCode.Code == Code.Ldarg_0)
                        {
                            anchorIndex = j;
                            break;
                        }
                    }
                    if (anchorIndex <= constructorIndex) continue;
                    if (!IsSingleByteOpcode(instructions[markerIndex])) continue;

                    // The replace range is [markerIndex, anchorIndex). The anchor remains in place.
                    // Internal branches disappear with the replaced bytes; only a surviving branch
                    // from OUTSIDE the range into its interior is unsafe.
                    EnsureSafeRawReplacement(method, instructions, markerIndex, anchorIndex);

                    int codeFileOffset = GetMethodCodeFileOffset(image, (uint)method.RVA);
                    int startIlOffset = checked((int)instructions[markerIndex].Offset);
                    int endIlOffset = checked((int)instructions[anchorIndex].Offset);
                    int length = endIlOffset - startIlOffset;
                    int fileOffset = checked(codeFileOffset + startIlOffset);

                    if (fileOffset < 0 || length < 5 || fileOffset > image.Length - length ||
                        codeFileOffset + endIlOffset > image.Length)
                        throw new InvalidDataException("下载页 IL 区域映射到文件时越界或长度不足。未生成文件。");
                    if (image[fileOffset] != (byte)instructions[markerIndex].OpCode.Value ||
                        image[checked(codeFileOffset + endIlOffset)] != (byte)instructions[anchorIndex].OpCode.Value)
                        throw new InvalidDataException("下载页指令与原始 PE 字节不一致，拒绝写入。");

                    result.Add(new LoaderPatchTarget
                    {
                        Method = method,
                        Start = instructions[markerIndex],
                        End = instructions[anchorIndex], // exclusive continuation anchor
                        LoaderField = loaderField,
                        FileOffset = fileOffset,
                        Length = length
                    });
                }
            }
            return result;
        }

        private static bool IsDownloadRestrictionMarker(Instruction instruction)
        {
            if (instruction == null || instruction.OpCode.Code != Code.Ldstr) return false;
            string text = instruction.Operand as string ?? string.Empty;
            return text.IndexOf("关闭下载", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("禁止下载", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (text.IndexOf("下载", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    text.IndexOf("法律法规", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static List<CardPatchTarget> FindMinecraftCardCollapsedPatterns(TypeDef pageModify, byte[] image)
        {
            var result = new List<CardPatchTarget>();
            foreach (MethodDef method in pageModify.Methods)
            {
                if (!method.HasBody) continue;
                IList<Instruction> instructions = method.Body.Instructions;
                for (int i = 0; i < instructions.Count; i++)
                {
                    if (!OperandContains(instructions[i], "get_CardMinecraft")) continue;
                    int constant = NextNonNop(instructions, i + 1);
                    if (constant < 0 || instructions[constant].OpCode.Code != Code.Ldc_I4_2) continue;
                    int setter = NextNonNop(instructions, constant + 1);
                    if (setter < 0 || !OperandContains(instructions[setter], "set_Visibility")) continue;

                    Instruction constantInstruction = instructions[constant];
                    if (!IsSingleByteOpcode(constantInstruction)) continue;
                    int codeFileOffset = GetMethodCodeFileOffset(image, (uint)method.RVA);
                    int fileOffset = checked(codeFileOffset + (int)constantInstruction.Offset);
                    if (fileOffset < 0 || fileOffset >= image.Length)
                        throw new InvalidDataException("Minecraft 卡片指令映射到文件时越界。未生成文件。");
                    if (image[fileOffset] != (byte)constantInstruction.OpCode.Value)
                        throw new InvalidDataException("Minecraft 卡片指令与原始文件字节不一致，拒绝写入。");

                    result.Add(new CardPatchTarget { Method = method, Constant = constantInstruction, FileOffset = fileOffset });
                }
            }
            return result;
        }

        private static void EnsureSafeRawReplacement(MethodDef method, IList<Instruction> instructions, int startIndex, int endExclusiveIndex)
        {
            if (startIndex < 0 || endExclusiveIndex <= startIndex || endExclusiveIndex > instructions.Count)
                throw new InvalidDataException("内部错误：IL 替换区间无效。");

            var replaced = new HashSet<Instruction>();
            for (int i = startIndex; i < endExclusiveIndex; i++) replaced.Add(instructions[i]);
            Instruction start = instructions[startIndex];
            var findings = new List<string>();

            // Branch instructions inside the replaced interval will themselves become NOPs, so
            // their outgoing edges disappear. Do NOT report their internal targets as errors.
            // Only surviving instructions outside the interval can create a dangling incoming edge.
            for (int sourceIndex = 0; sourceIndex < instructions.Count; sourceIndex++)
            {
                if (sourceIndex >= startIndex && sourceIndex < endExclusiveIndex) continue;
                Instruction source = instructions[sourceIndex];
                if (source.Operand is Instruction target && replaced.Contains(target) && target != start)
                {
                    findings.Add("外部指令#" + sourceIndex + " " + source.OpCode.Name +
                        " 跳入待替换区间内部指令#" + instructions.IndexOf(target));
                }
                else if (source.Operand is IList<Instruction> targets)
                {
                    foreach (Instruction targetItem in targets)
                    {
                        if (replaced.Contains(targetItem) && targetItem != start)
                            findings.Add("外部 switch 指令#" + sourceIndex + " 跳入待替换区间内部指令#" + instructions.IndexOf(targetItem));
                    }
                }
            }

            // A try/handler/filter boundary inside the interval would still point into bytes whose
            // instruction layout is changing. The code length stays constant, but we refuse such cases.
            foreach (ExceptionHandler eh in method.Body.ExceptionHandlers)
            {
                Instruction[] boundaries = { eh.TryStart, eh.TryEnd, eh.HandlerStart, eh.HandlerEnd, eh.FilterStart };
                foreach (Instruction boundary in boundaries)
                {
                    if (boundary != null && replaced.Contains(boundary) && boundary != start)
                        findings.Add("异常处理边界位于区间内部的指令#" + instructions.IndexOf(boundary));
                }
            }

            if (findings.Count > 0)
                throw new InvalidDataException("待替换区间存在从外部进入内部的分支或异常处理边界（" + method.FullName + "）：" +
                    string.Join("；", findings) + "。未生成文件。");
        }

        private static void VerifyPatchedStructure(ModuleDef module, Action<string> log)
        {
            TypeDef pageInstall = RequireType(module, "PCL.PageDownloadInstall");
            TypeDef modDownload = RequireType(module, "PCL.ModDownload");
            TypeDef pageModify = RequireType(module, "PCL.PageDownloadModify");
            FieldDef loaderField = modDownload.Fields.SingleOrDefault(f =>
                string.Equals(f.Name.ToString(), "DlClientListLoader", StringComparison.Ordinal));
            if (loaderField == null) throw new InvalidDataException("输出复检失败：找不到 DlClientListLoader 字段。");

            List<MethodDef> loaderMethods = pageInstall.Methods.Where(m => m.HasBody &&
                m.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldsfld && ReferenceEquals(i.Operand, loaderField)) &&
                m.Body.Instructions.Any(i => OperandContains(i, "PageLoaderInit"))).ToList();
            if (loaderMethods.Count != 1)
                throw new InvalidDataException("输出复检失败：未能唯一定位已恢复的加载器引用；匹配数=" + loaderMethods.Count + ".");

            int cardVisible = 0;
            foreach (MethodDef method in pageModify.Methods)
            {
                if (!method.HasBody) continue;
                IList<Instruction> instructions = method.Body.Instructions;
                for (int i = 0; i < instructions.Count; i++)
                {
                    if (!OperandContains(instructions[i], "get_CardMinecraft")) continue;
                    int constant = NextNonNop(instructions, i + 1);
                    int setter = constant < 0 ? -1 : NextNonNop(instructions, constant + 1);
                    if (constant >= 0 && setter >= 0 && instructions[constant].OpCode.Code == Code.Ldc_I4_0 &&
                        OperandContains(instructions[setter], "set_Visibility")) cardVisible++;
                }
            }
            if (cardVisible != 1)
                throw new InvalidDataException("输出复检失败：Minecraft 卡片 Visible 指令模式匹配数=" + cardVisible + "，应为 1。");
            log("输出程序集复检通过：找到已恢复的静态加载器字段引用，并确认 MC 卡片可见性常量已更改。");
        }

        private static bool HasCallAfter(IList<Instruction> instructions, int start, string methodName)
        {
            for (int i = start; i < instructions.Count; i++)
            {
                Code code = instructions[i].OpCode.Code;
                if ((code == Code.Call || code == Code.Callvirt || code == Code.Calli) && OperandContains(instructions[i], methodName))
                    return true;
            }
            return false;
        }

        private static int NextNonNop(IList<Instruction> instructions, int start)
        {
            for (int i = start; i < instructions.Count; i++)
                if (instructions[i].OpCode.Code != Code.Nop) return i;
            return -1;
        }

        private static bool IsSingleByteOpcode(Instruction instruction)
        {
            return instruction != null && instruction.OpCode.Value >= 0 && instruction.OpCode.Value <= 0xFF;
        }

        private static TypeDef RequireType(ModuleDef module, string fullName)
        {
            List<TypeDef> matches = module.GetTypes().Where(t => string.Equals(t.FullName, fullName, StringComparison.Ordinal)).ToList();
            if (matches.Count != 1)
                throw new InvalidDataException("类型定位失败：" + fullName + "（匹配数=" + matches.Count + "）。当前 EXE 可能并非同类 PCL 构建。未生成文件。");
            return matches[0];
        }

        private static bool OperandContains(Instruction instruction, string text)
        {
            return instruction != null && instruction.Operand != null &&
                   instruction.Operand.ToString().IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string DescribeLoaderMethods(TypeDef pageInstall)
        {
            var lines = new List<string>();
            foreach (MethodDef method in pageInstall.Methods.Where(m => m.HasBody))
            {
                IList<Instruction> ins = method.Body.Instructions;
                bool mentionsLoader = ins.Any(i => OperandContains(i, "LoaderTask"));
                bool mentionsPageLoader = ins.Any(i => OperandContains(i, "PageLoaderInit"));
                bool mentionsMarker = ins.Any(IsDownloadRestrictionMarker);
                if (mentionsLoader || mentionsPageLoader || mentionsMarker)
                    lines.Add("  " + method.FullName + "；IL指令=" + ins.Count + "；限制文本=" + mentionsMarker + "；LoaderTask=" + mentionsLoader + "；PageLoaderInit=" + mentionsPageLoader);
            }
            return lines.Count == 0 ? "PCL.PageDownloadInstall 中没有发现 LoaderTask/PageLoaderInit/关闭下载相关特征。" : string.Join(Environment.NewLine, lines);
        }

        private static string DescribeCardMethods(TypeDef pageModify)
        {
            var lines = new List<string>();
            foreach (MethodDef method in pageModify.Methods.Where(m => m.HasBody))
            {
                if (method.Body.Instructions.Any(i => OperandContains(i, "get_CardMinecraft") || OperandContains(i, "set_Visibility")))
                    lines.Add("  " + method.FullName + "；IL指令=" + method.Body.Instructions.Count);
            }
            return lines.Count == 0 ? "PageDownloadModify 中未发现 Minecraft 卡片或 Visibility 相关方法。" : string.Join(Environment.NewLine, lines);
        }

        private static int GetMethodCodeFileOffset(byte[] image, uint methodRva)
        {
            int methodHeaderOffset = RvaToFileOffset(image, methodRva);
            if (methodHeaderOffset < 0 || methodHeaderOffset >= image.Length)
                throw new InvalidDataException("方法 RVA 无法映射到文件偏移。未生成文件。");

            int kind = image[methodHeaderOffset] & 0x03;
            if (kind == 0x02)
                return checked(methodHeaderOffset + 1); // tiny header
            if (kind == 0x03)
            {
                ushort flagsAndSize = ReadUInt16(image, methodHeaderOffset);
                int headerSize = ((flagsAndSize >> 12) & 0x0F) * 4;
                if (headerSize < 12 || methodHeaderOffset + headerSize > image.Length)
                    throw new InvalidDataException("方法 fat header 大小无效。未生成文件。");
                return checked(methodHeaderOffset + headerSize);
            }
            throw new InvalidDataException("无法识别方法头格式。目标 EXE 可能不是受支持的托管程序集。");
        }

        private static int RvaToFileOffset(byte[] image, uint rva)
        {
            if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
                throw new InvalidDataException("输入文件不是 PE 文件。");
            int peOffset = BitConverter.ToInt32(image, 0x3C);
            if (peOffset < 0 || peOffset + 24 > image.Length || image[peOffset] != (byte)'P' || image[peOffset + 1] != (byte)'E' ||
                image[peOffset + 2] != 0 || image[peOffset + 3] != 0)
                throw new InvalidDataException("PE 签名无效。");

            ushort sectionCount = ReadUInt16(image, peOffset + 6);
            ushort optionalHeaderSize = ReadUInt16(image, peOffset + 20);
            int firstSection = checked(peOffset + 24 + optionalHeaderSize);
            if (firstSection + sectionCount * 40 > image.Length) throw new InvalidDataException("PE 节表越界。");

            for (int i = 0; i < sectionCount; i++)
            {
                int section = firstSection + i * 40;
                uint virtualAddress = ReadUInt32(image, section + 12);
                uint rawSize = ReadUInt32(image, section + 16);
                uint rawPointer = ReadUInt32(image, section + 20);
                if (rva < virtualAddress) continue;
                ulong delta = (ulong)rva - virtualAddress;
                if (delta >= rawSize) continue;
                ulong fileOffset = (ulong)rawPointer + delta;
                if (fileOffset >= (ulong)image.Length) throw new InvalidDataException("RVA 映射结果超出文件范围。");
                return checked((int)fileOffset);
            }
            throw new InvalidDataException("找不到包含 RVA 0x" + rva.ToString("X8") + " 的 PE 节。");
        }

        private static ushort ReadUInt16(byte[] data, int offset)
        {
            if (offset < 0 || offset + 2 > data.Length) throw new InvalidDataException("读取 PE 头时越界。");
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            if (offset < 0 || offset + 4 > data.Length) throw new InvalidDataException("读取 PE 头时越界。");
            return (uint)data[offset] | ((uint)data[offset + 1] << 8) | ((uint)data[offset + 2] << 16) | ((uint)data[offset + 3] << 24);
        }

        private static void EnsureOnlyApprovedRangesChanged(byte[] before, byte[] after, int loaderOffset, int loaderLength, int cardOffset)
        {
            if (before.Length != after.Length) throw new InvalidDataException("安全检查失败：输出大小改变。");
            for (int i = 0; i < before.Length; i++)
            {
                if (before[i] == after[i]) continue;
                bool inLoader = i >= loaderOffset && i < loaderOffset + loaderLength;
                if (!inLoader && i != cardOffset)
                    throw new InvalidDataException("安全检查失败：发现目标区域以外的字节差异，偏移=0x" + i.ToString("X") + "。未生成文件。");
            }
        }
    }
}
