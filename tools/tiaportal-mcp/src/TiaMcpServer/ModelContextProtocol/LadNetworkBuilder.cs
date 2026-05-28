using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// LAD FlgNet/v5 真梯形图网络构造器：触点 / 线圈 / 串联 / 并联（OR 汇合）。
    /// 不依赖 FC 调用 — 可独立构成自保持/互锁/比较输出 等真实 LAD 程序段。
    ///
    /// 抽象模型：rung 是从 Powerrail 出发，向右走一连串 RungElement，最终连到 Coil。
    /// - Contact：NO/NC 触点，串行加入 rung 当前位置
    /// - Parallel：在 rung 当前位置分叉若干条 sub-rung，OR 汇合
    /// - Coil：rung 结尾的输出线圈（普通 / S / R）
    /// </summary>
    public sealed class LadNetworkBuilder
    {
        private static readonly XNamespace FlgNetNs = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";

        private int _uid = 21;
        private readonly List<XElement> _accesses = new();
        private readonly List<XElement> _parts = new();
        private readonly List<XElement> _wires = new();

        /// <summary>构造完整 FlgNet 网络元素（含命名空间）</summary>
        public XElement Build(IEnumerable<LadRungElement> rungElements)
        {
            var rung = (rungElements ?? throw new ArgumentNullException(nameof(rungElements))).ToList();
            if (rung.Count == 0)
                throw new ArgumentException("LAD rung 至少需要 1 个元素", nameof(rungElements));

            // 当前连接端点：起始为 Powerrail
            ConnPoint current = ConnPoint.PowerRail();

            for (var i = 0; i < rung.Count; i++)
            {
                var el = rung[i];
                current = EmitElement(el, current, isLast: i == rung.Count - 1);
            }

            return new XElement(FlgNetNs + "FlgNet",
                new XElement(FlgNetNs + "Parts", _accesses.Concat(_parts)),
                new XElement(FlgNetNs + "Wires", _wires));
        }

        public string BuildXml(IEnumerable<LadRungElement> rungElements)
        {
            return Build(rungElements).ToString(SaveOptions.None);
        }

        private ConnPoint EmitElement(LadRungElement el, ConnPoint prev, bool isLast)
        {
            switch (el)
            {
                case LadContact c: return EmitContact(c, prev);
                case LadCoil co:   EmitCoil(co, prev); return ConnPoint.None();
                case LadParallel par: return EmitParallel(par, prev);
                default:
                    throw new ArgumentException("Unknown LAD rung element: " + el.GetType().Name);
            }
        }

        private ConnPoint EmitContact(LadContact c, ConnPoint prev)
        {
            // Access(operand)
            var operandUid = NextUid();
            _accesses.Add(BuildAccess(operandUid, c.Operand));

            // Part(Contact) [+ Negated]
            var partUid = NextUid();
            var part = new XElement(FlgNetNs + "Part",
                new XAttribute("Name", "Contact"),
                new XAttribute("UId", partUid));
            if (c.Negated)
                part.Add(new XElement(FlgNetNs + "Negated", new XAttribute("Name", "operand")));
            _parts.Add(part);

            // Wire: prev → contact.in
            _wires.Add(BuildWire(NextUid(), prev, ConnPoint.Pin(partUid, "in")));
            // Wire: operand-access → contact.operand
            _wires.Add(BuildWire(NextUid(), ConnPoint.Ident(operandUid), ConnPoint.Pin(partUid, "operand")));

            return ConnPoint.Pin(partUid, "out");
        }

        private void EmitCoil(LadCoil co, ConnPoint prev)
        {
            var coilName = co.Type switch
            {
                LadCoilType.Set   => "SCoil",
                LadCoilType.Reset => "RCoil",
                _                 => "Coil"
            };

            var operandUid = NextUid();
            _accesses.Add(BuildAccess(operandUid, co.Operand));

            var partUid = NextUid();
            _parts.Add(new XElement(FlgNetNs + "Part",
                new XAttribute("Name", coilName),
                new XAttribute("UId", partUid)));

            _wires.Add(BuildWire(NextUid(), prev, ConnPoint.Pin(partUid, "in")));
            _wires.Add(BuildWire(NextUid(), ConnPoint.Ident(operandUid), ConnPoint.Pin(partUid, "operand")));
        }

        private ConnPoint EmitParallel(LadParallel par, ConnPoint prev)
        {
            if (par.Branches == null || par.Branches.Count < 2)
                throw new ArgumentException("LAD 并联至少需要 2 条分支");

            // 每条分支的末尾端点
            var branchEnds = new List<ConnPoint>();
            foreach (var branch in par.Branches)
            {
                if (branch.Count == 0)
                    throw new ArgumentException("LAD 并联分支不能为空");
                ConnPoint cur = prev;
                foreach (var sub in branch)
                {
                    cur = EmitElement(sub, cur, false);
                    if (cur.Kind == ConnPointKind.None)
                        throw new ArgumentException("LAD 并联分支中不能含 Coil");
                }
                branchEnds.Add(cur);
            }

            // O 汇合 Part：in1, in2, ..., out
            var oUid = NextUid();
            _parts.Add(new XElement(FlgNetNs + "Part",
                new XAttribute("Name", "O"),
                new XAttribute("UId", oUid)));

            for (var i = 0; i < branchEnds.Count; i++)
            {
                _wires.Add(BuildWire(NextUid(), branchEnds[i], ConnPoint.Pin(oUid, "in" + (i + 1))));
            }

            return ConnPoint.Pin(oUid, "out");
        }

        // ────────────────────── Access 构造 ──────────────────────
        // 复用 SCL 的 Symbol 解析约定：含 "  → 全局；无 " 含 . → 局部多段；纯名字 → 局部
        private XElement BuildAccess(int uid, string operandSpec)
        {
            if (string.IsNullOrWhiteSpace(operandSpec))
                throw new ArgumentException("LAD operand 不能为空");
            var trimmed = operandSpec.Trim();
            if (trimmed.StartsWith("#")) trimmed = trimmed.Substring(1);
            var hasQuote = trimmed.Contains('"');
            var stripped = trimmed.Replace("\"", "");
            var parts = stripped.Split('.');

            var scope = hasQuote ? "GlobalVariable" : "LocalVariable";
            var symbol = new XElement(FlgNetNs + "Symbol");
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) symbol.Add(new XElement(FlgNetNs + "Token", new XAttribute("Text", ".")));
                var comp = new XElement(FlgNetNs + "Component", new XAttribute("Name", parts[i]));
                if (hasQuote && i == 0 && parts.Length == 1)
                {
                    // 单段全局：HasQuotes
                    comp.Add(new XElement(FlgNetNs + "BooleanAttribute",
                        new XAttribute("Name", "HasQuotes"),
                        new XAttribute("SystemDefined", "true"),
                        "true"));
                }
                symbol.Add(comp);
            }
            return new XElement(FlgNetNs + "Access",
                new XAttribute("Scope", scope),
                new XAttribute("UId", uid),
                symbol);
        }

        // ────────────────────── Wire 构造 ──────────────────────
        private XElement BuildWire(int uid, ConnPoint from, ConnPoint to)
        {
            return new XElement(FlgNetNs + "Wire",
                new XAttribute("UId", uid),
                ConnElement(from),
                ConnElement(to));
        }

        private XElement ConnElement(ConnPoint p)
        {
            return p.Kind switch
            {
                ConnPointKind.PowerRail => new XElement(FlgNetNs + "Powerrail"),
                ConnPointKind.NameCon   => new XElement(FlgNetNs + "NameCon",
                    new XAttribute("UId", p.Uid),
                    new XAttribute("Name", p.PinName)),
                ConnPointKind.IdentCon  => new XElement(FlgNetNs + "IdentCon",
                    new XAttribute("UId", p.Uid)),
                _ => throw new InvalidOperationException("Cannot wire to End point")
            };
        }

        private int NextUid() => _uid++;

        // ────────────────────── 内部端点类型 ──────────────────────
        private enum ConnPointKind { None, PowerRail, NameCon, IdentCon }
        private struct ConnPoint
        {
            public ConnPointKind Kind;
            public int Uid;
            public string PinName;
            public static ConnPoint None() => new() { Kind = ConnPointKind.None };
            public static ConnPoint PowerRail() => new() { Kind = ConnPointKind.PowerRail };
            public static ConnPoint Pin(int uid, string name) => new() { Kind = ConnPointKind.NameCon, Uid = uid, PinName = name };
            public static ConnPoint Ident(int uid) => new() { Kind = ConnPointKind.IdentCon, Uid = uid };
        }
    }

    // ────────────────────── 公开 JSON 数据模型 ──────────────────────
    public abstract class LadRungElement {}
    public sealed class LadContact : LadRungElement
    {
        public LadContact(string operand, bool negated = false)
        {
            Operand = operand ?? throw new ArgumentNullException(nameof(operand));
            Negated = negated;
        }
        public string Operand { get; }
        public bool Negated { get; }
    }
    public enum LadCoilType { Coil, Set, Reset }
    public sealed class LadCoil : LadRungElement
    {
        public LadCoil(string operand, LadCoilType type = LadCoilType.Coil)
        {
            Operand = operand ?? throw new ArgumentNullException(nameof(operand));
            Type = type;
        }
        public string Operand { get; }
        public LadCoilType Type { get; }
    }
    public sealed class LadParallel : LadRungElement
    {
        public LadParallel(IEnumerable<IEnumerable<LadRungElement>> branches)
        {
            Branches = branches?.Select(b => b.ToList()).ToList() ?? throw new ArgumentNullException(nameof(branches));
        }
        public List<List<LadRungElement>> Branches { get; }
    }
}
