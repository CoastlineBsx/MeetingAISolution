using System.Collections.Generic;
using System.Linq;

namespace MeetingAI.Host;

/// <summary>
/// 字段定义
/// </summary>
public class FieldDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = "string"; // string, number, boolean, array, object
    public string Description { get; set; } = string.Empty;
    public List<FieldDefinition>? SubFields { get; set; }
}

/// <summary>
/// 文档类型模板
/// </summary>
public class DocumentTypeTemplate
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public List<FieldDefinition> Fields { get; set; } = new();
    public string ExampleJson { get; set; } = string.Empty;
}

/// <summary>
/// IE模板库：11个预设文档类型
/// </summary>
public static class IETemplates
{
    /// <summary>
    /// 所有预设模板
    /// </summary>
    public static List<DocumentTypeTemplate> AllTemplates { get; } = new()
    {
        // 1. 简历/求职
        new DocumentTypeTemplate
        {
            Id = "resume",
            Name = "简历/求职",
            Icon = "📄",
            Fields = new List<FieldDefinition>
            {
                new() { Key = "name", Label = "姓名", Type = "string", Description = "应聘者姓名" },
                new() { Key = "gender", Label = "性别", Type = "string", Description = "性别" },
                new() { Key = "birth_date", Label = "出生日期", Type = "string", Description = "出生日期" },
                new() { Key = "phone", Label = "电话", Type = "string", Description = "联系电话" },
                new() { Key = "email", Label = "邮箱", Type = "string", Description = "电子邮箱" },
                new() { Key = "location", Label = "现居地", Type = "string", Description = "现居住地" },
                new() { Key = "education", Label = "最高学历", Type = "string", Description = "最高学历（本科/硕士/博士）" },
                new() { Key = "university", Label = "毕业院校", Type = "string", Description = "毕业学校" },
                new() { Key = "major", Label = "专业", Type = "string", Description = "所学专业" },
                new() { Key = "graduation_date", Label = "毕业时间", Type = "string", Description = "毕业时间" },
                new() { Key = "work_experience", Label = "工作经历", Type = "array", Description = "工作经历列表",
                    SubFields = new List<FieldDefinition>
                    {
                        new() { Key = "company", Label = "公司", Type = "string" },
                        new() { Key = "position", Label = "职位", Type = "string" },
                        new() { Key = "duration", Label = "时间段", Type = "string" },
                        new() { Key = "description", Label = "工作内容", Type = "string" }
                    }
                },
                new() { Key = "project_experience", Label = "项目经验", Type = "array", Description = "项目经验列表",
                    SubFields = new List<FieldDefinition>
                    {
                        new() { Key = "name", Label = "项目名称", Type = "string" },
                        new() { Key = "role", Label = "角色", Type = "string" },
                        new() { Key = "duration", Label = "时间", Type = "string" },
                        new() { Key = "tech_stack", Label = "技术栈", Type = "string" },
                        new() { Key = "achievement", Label = "成果", Type = "string" }
                    }
                },
                new() { Key = "skills", Label = "专业技能", Type = "string", Description = "专业技能列表" },
                new() { Key = "languages", Label = "语言能力", Type = "string", Description = "语言能力" },
                new() { Key = "certificates", Label = "证书", Type = "string", Description = "证书列表" },
                new() { Key = "expected_position", Label = "期望岗位", Type = "string", Description = "期望岗位" },
                new() { Key = "expected_salary", Label = "期望薪资", Type = "string", Description = "期望薪资" },
            },
            ExampleJson = @"{""name"":""张三"",""phone"":""13800138000"",""email"":""zhangsan@example.com"",""education"":""硕士"",""work_experience"":[{""company"":""ABC公司"",""position"":""工程师"",""duration"":""2020-2022""}]}"
        },

        // 2. 合同/协议
        new DocumentTypeTemplate
        {
            Id = "contract",
            Name = "合同/协议",
            Icon = "📜",
            Fields = new List<FieldDefinition>
            {
                new() { Key = "contract_number", Label = "合同编号", Type = "string", Description = "合同编号" },
                new() { Key = "contract_name", Label = "合同名称", Type = "string", Description = "合同名称" },
                new() { Key = "sign_date", Label = "签订日期", Type = "string", Description = "签订日期" },
                new() { Key = "effective_date", Label = "生效日期", Type = "string", Description = "生效日期" },
                new() { Key = "termination_date", Label = "终止日期", Type = "string", Description = "终止日期" },
                new() { Key = "party_a_name", Label = "甲方名称", Type = "string", Description = "甲方名称" },
                new() { Key = "party_b_name", Label = "乙方名称", Type = "string", Description = "乙方名称" },
                new() { Key = "party_a_contact", Label = "甲方联系人", Type = "string", Description = "甲方联系人" },
                new() { Key = "party_b_contact", Label = "乙方联系人", Type = "string", Description = "乙方联系人" },
                new() { Key = "total_amount", Label = "合同总额", Type = "string", Description = "合同总金额" },
                new() { Key = "currency", Label = "币种", Type = "string", Description = "货币类型" },
                new() { Key = "payment_method", Label = "付款方式", Type = "string", Description = "付款方式" },
                new() { Key = "payment_schedule", Label = "付款节点", Type = "string", Description = "付款时间节点" },
                new() { Key = "deliverables", Label = "交付物", Type = "string", Description = "交付物描述" },
                new() { Key = "delivery_time", Label = "交付时间", Type = "string", Description = "交付时间" },
                new() { Key = "acceptance_criteria", Label = "验收标准", Type = "string", Description = "验收标准" },
                new() { Key = "breach_liability_a", Label = "甲方违约责任", Type = "string", Description = "甲方违约责任" },
                new() { Key = "breach_liability_b", Label = "乙方违约责任", Type = "string", Description = "乙方违约责任" },
                new() { Key = "penalty_calculation", Label = "违约金计算", Type = "string", Description = "违约金计算方式" },
                new() { Key = "dispute_resolution", Label = "争议解决", Type = "string", Description = "争议解决方式" },
            },
            ExampleJson = @"{""contract_number"":""HT2024001"",""party_a_name"":""ABC公司"",""party_b_name"":""XYZ公司"",""total_amount"":""100万元""}"
        },

        // 3. 发票/收据
        new DocumentTypeTemplate
        {
            Id = "invoice",
            Name = "发票/收据",
            Icon = "🧾",
            Fields = new List<FieldDefinition>
            {
                new() { Key = "invoice_number", Label = "发票号码", Type = "string", Description = "发票号码" },
                new() { Key = "invoice_code", Label = "发票代码", Type = "string", Description = "发票代码" },
                new() { Key = "invoice_date", Label = "开票日期", Type = "string", Description = "开票日期" },
                new() { Key = "invoice_type", Label = "发票类型", Type = "string", Description = "发票类型" },
                new() { Key = "buyer_name", Label = "购买方名称", Type = "string", Description = "购买方名称" },
                new() { Key = "buyer_tax_number", Label = "购买方税号", Type = "string", Description = "购买方纳税人识别号" },
                new() { Key = "seller_name", Label = "销售方名称", Type = "string", Description = "销售方名称" },
                new() { Key = "seller_tax_number", Label = "销售方税号", Type = "string", Description = "销售方纳税人识别号" },
                new() { Key = "total_amount", Label = "金额合计", Type = "string", Description = "价税合计前的金额" },
                new() { Key = "total_tax", Label = "税额", Type = "string", Description = "税额" },
                new() { Key = "total_with_tax", Label = "价税合计", Type = "string", Description = "价税合计" },
                new() { Key = "items", Label = "商品明细", Type = "array", Description = "商品明细列表",
                    SubFields = new List<FieldDefinition>
                    {
                        new() { Key = "name", Label = "商品名称", Type = "string" },
                        new() { Key = "specification", Label = "规格型号", Type = "string" },
                        new() { Key = "quantity", Label = "数量", Type = "string" },
                        new() { Key = "unit_price", Label = "单价", Type = "string" },
                        new() { Key = "amount", Label = "金额", Type = "string" }
                    }
                },
                new() { Key = "remarks", Label = "备注", Type = "string", Description = "发票备注信息" },
            },
            ExampleJson = @"{""invoice_number"":""12345678"",""buyer_name"":""ABC公司"",""total_with_tax"":""1130元""}"
        },

        // 4. 新闻/报道
        new DocumentTypeTemplate
        {
            Id = "news",
            Name = "新闻/报道",
            Icon = "📰",
            Fields = new List<FieldDefinition>
            {
                new() { Key = "title", Label = "标题", Type = "string", Description = "新闻标题" },
                new() { Key = "author", Label = "作者", Type = "string", Description = "作者/记者" },
                new() { Key = "publish_time", Label = "发布时间", Type = "string", Description = "发布时间" },
                new() { Key = "source", Label = "来源", Type = "string", Description = "来源媒体" },
                new() { Key = "event_time", Label = "事件时间", Type = "string", Description = "事件发生时间" },
                new() { Key = "event_location", Label = "事件地点", Type = "string", Description = "事件发生地点" },
                new() { Key = "event_people", Label = "相关人物", Type = "string", Description = "涉及的人物" },
                new() { Key = "event_description", Label = "事件描述", Type = "string", Description = "事件经过描述" },
                new() { Key = "event_impact", Label = "影响范围", Type = "string", Description = "事件影响范围" },
                new() { Key = "key_data", Label = "关键数据", Type = "string", Description = "相关数据和统计" },
                new() { Key = "expert_opinion", Label = "专家观点", Type = "string", Description = "专家评论和观点" },
                new() { Key = "official_statement", Label = "官方声明", Type = "string", Description = "官方声明或回应" },
            },
            ExampleJson = @"{""title"":""某地发生重大事件"",""event_time"":""2024年1月1日"",""event_location"":""北京""}"
        },

        // 5. 学术论文
        new DocumentTypeTemplate
        {
            Id = "paper",
            Name = "学术论文",
            Icon = "📚",
            Fields = new List<FieldDefinition>
            {
                new() { Key = "title", Label = "标题", Type = "string", Description = "论文标题" },
                new() { Key = "authors", Label = "作者", Type = "string", Description = "作者列表" },
                new() { Key = "institutions", Label = "作者单位", Type = "string", Description = "作者所属机构" },
                new() { Key = "journal", Label = "发表期刊", Type = "string", Description = "发表期刊名称" },
                new() { Key = "publish_time", Label = "发表时间", Type = "string", Description = "发表时间" },
                new() { Key = "doi", Label = "DOI", Type = "string", Description = "数字对象标识符" },
                new() { Key = "abstract_zh", Label = "中文摘要", Type = "string", Description = "中文摘要" },
                new() { Key = "abstract_en", Label = "英文摘要", Type = "string", Description = "英文摘要" },
                new() { Key = "keywords", Label = "关键词", Type = "string", Description = "关键词列表" },
                new() { Key = "research_background", Label = "研究背景", Type = "string", Description = "研究背景介绍" },
                new() { Key = "research_method", Label = "研究方法", Type = "string", Description = "研究方法描述" },
                new() { Key = "main_conclusion", Label = "主要结论", Type = "string", Description = "主要研究结论" },
                new() { Key = "innovation", Label = "创新点", Type = "string", Description = "主要创新点" },
            },
            ExampleJson = @"{""title"":""基于深度学习的....."",""authors"":""张三,李四"",""keywords"":""深度学习,神经网络""}"
        },

        // 6. 产品说明书
        new DocumentTypeTemplate
        {
            Id = "manual",
            Name = "产品说明书",
            Icon = "📦",
            Fields = new List<FieldDefinition>
            {
                new() { Key = "product_name", Label = "产品名称", Type = "string", Description = "产品名称" },
                new() { Key = "model", Label = "型号", Type = "string", Description = "产品型号" },
                new() { Key = "manufacturer", Label = "制造商", Type = "string", Description = "制造商名称" },
                new() { Key = "production_date", Label = "生产日期", Type = "string", Description = "生产日期" },
                new() { Key = "dimensions", Label = "尺寸", Type = "string", Description = "产品尺寸" },
                new() { Key = "weight", Label = "重量", Type = "string", Description = "产品重量" },
                new() { Key = "power", Label = "功率", Type = "string", Description = "功率参数" },
                new() { Key = "specifications", Label = "性能参数", Type = "string", Description = "主要性能参数" },
                new() { Key = "installation", Label = "安装步骤", Type = "string", Description = "安装步骤说明" },
                new() { Key = "operation", Label = "操作方法", Type = "string", Description = "操作使用方法" },
                new() { Key = "precautions", Label = "注意事项", Type = "string", Description = "使用注意事项" },
                new() { Key = "maintenance", Label = "维护保养", Type = "string", Description = "日常维护方法" },
                new() { Key = "troubleshooting", Label = "故障排除", Type = "string", Description = "常见故障及解决方法" },
                new() { Key = "warranty", Label = "保修信息", Type = "string", Description = "保修条款和期限" },
                new() { Key = "safety_warning", Label = "安全警告", Type = "string", Description = "安全警告和禁止操作" },
            },
            ExampleJson = @"{""product_name"":""智能空调"",""model"":""KFR-35GW"",""manufacturer"":""某某电器""}"
        },

        // 7. 法律文书
        new DocumentTypeTemplate
        {
            Id = "legal",
            Name = "法律文书",
            Icon = "⚖️",
            Fields = new List<FieldDefinition>
            {
                new() { Key = "document_type", Label = "文书类型", Type = "string", Description = "文书类型（判决书/裁定书/调解书等）" },
                new() { Key = "case_number", Label = "案号", Type = "string", Description = "案件编号" },
                new() { Key = "court", Label = "审理法院", Type = "string", Description = "审理法院名称" },
                new() { Key = "trial_date", Label = "审理日期", Type = "string", Description = "开庭审理日期" },
                new() { Key = "plaintiff", Label = "原告/申请人", Type = "string", Description = "原告或申请人信息" },
                new() { Key = "defendant", Label = "被告/被申请人", Type = "string", Description = "被告或被申请人信息" },
                new() { Key = "plaintiff_lawyer", Label = "原告代理律师", Type = "string", Description = "原告代理律师" },
                new() { Key = "defendant_lawyer", Label = "被告代理律师", Type = "string", Description = "被告代理律师" },
                new() { Key = "case_cause", Label = "案由", Type = "string", Description = "案件原因" },
                new() { Key = "dispute_focus", Label = "争议焦点", Type = "string", Description = "主要争议焦点" },
                new() { Key = "case_facts", Label = "案件事实", Type = "string", Description = "认定的案件事实" },
                new() { Key = "legal_basis", Label = "法律依据", Type = "string", Description = "适用的法律条文" },
                new() { Key = "judgment", Label = "判决主文", Type = "string", Description = "判决结果" },
                new() { Key = "execution", Label = "执行内容", Type = "string", Description = "需要执行的内容" },
                new() { Key = "appeal_period", Label = "上诉期限", Type = "string", Description = "上诉期限" },
            },
            ExampleJson = @"{""case_number"":""(2024)京01民初123号"",""court"":""北京市第一中级人民法院"",""case_cause"":""合同纠纷""}"
        },

        // 8. 财务报表
        new DocumentTypeTemplate
        {
            Id = "financial",
            Name = "财务报表",
            Icon = "💰",
            Fields = new List<FieldDefinition>
            {
                new() { Key = "report_type", Label = "报表类型", Type = "string", Description = "报表类型（资产负债表/利润表/现金流量表）" },
                new() { Key = "report_period", Label = "报表期间", Type = "string", Description = "报表所属期间" },
                new() { Key = "company_name", Label = "编制单位", Type = "string", Description = "编制单位名称" },
                new() { Key = "currency", Label = "币种", Type = "string", Description = "货币单位" },
                new() { Key = "current_assets", Label = "流动资产", Type = "string", Description = "流动资产总额" },
                new() { Key = "non_current_assets", Label = "非流动资产", Type = "string", Description = "非流动资产总额" },
                new() { Key = "total_assets", Label = "资产总计", Type = "string", Description = "资产总计" },
                new() { Key = "current_liabilities", Label = "流动负债", Type = "string", Description = "流动负债总额" },
                new() { Key = "non_current_liabilities", Label = "非流动负债", Type = "string", Description = "非流动负债总额" },
                new() { Key = "total_liabilities", Label = "负债总计", Type = "string", Description = "负债总计" },
                new() { Key = "equity", Label = "所有者权益", Type = "string", Description = "所有者权益总额" },
                new() { Key = "net_assets", Label = "净资产", Type = "string", Description = "净资产" },
                new() { Key = "revenue", Label = "营业收入", Type = "string", Description = "营业收入" },
                new() { Key = "cost", Label = "营业成本", Type = "string", Description = "营业成本" },
                new() { Key = "net_profit", Label = "净利润", Type = "string", Description = "净利润" },
            },
            ExampleJson = @"{""report_type"":""资产负债表"",""report_period"":""2024年第一季度"",""total_assets"":""1000万元""}"
        },

        // 9. 邮件/信函
        new DocumentTypeTemplate
        {
            Id = "email",
            Name = "邮件/信函",
            Icon = "✉️",
            Fields = new List<FieldDefinition>
            {
                new() { Key = "sender", Label = "发件人", Type = "string", Description = "发件人姓名或邮箱" },
                new() { Key = "recipient", Label = "收件人", Type = "string", Description = "收件人姓名或邮箱" },
                new() { Key = "cc", Label = "抄送", Type = "string", Description = "抄送人列表" },
                new() { Key = "send_time", Label = "发送时间", Type = "string", Description = "邮件发送时间" },
                new() { Key = "subject", Label = "主题", Type = "string", Description = "邮件主题" },
                new() { Key = "body", Label = "正文内容", Type = "string", Description = "邮件正文" },
                new() { Key = "key_request", Label = "关键诉求", Type = "string", Description = "邮件中的主要诉求" },
                new() { Key = "attachments", Label = "附件", Type = "string", Description = "附件列表和说明" },
                new() { Key = "questions_to_reply", Label = "需要回复的问题", Type = "string", Description = "需要回复的具体问题" },
                new() { Key = "action_items", Label = "待办事项", Type = "string", Description = "待办事项列表" },
                new() { Key = "deadline", Label = "截止日期", Type = "string", Description = "事项截止日期" },
            },
            ExampleJson = @"{""sender"":""zhangsan@company.com"",""subject"":""关于项目进度的讨论"",""send_time"":""2024-01-15 10:30""}"
        },

        // 10. 会议纪要
        new DocumentTypeTemplate
        {
            Id = "meeting",
            Name = "会议纪要",
            Icon = "🎤",
            Fields = new List<FieldDefinition>
            {
                new() { Key = "meeting_title", Label = "会议主题", Type = "string", Description = "会议主题或名称" },
                new() { Key = "meeting_time", Label = "会议时间", Type = "string", Description = "会议开始和结束时间" },
                new() { Key = "meeting_location", Label = "会议地点", Type = "string", Description = "会议地点" },
                new() { Key = "host", Label = "主持人", Type = "string", Description = "会议主持人" },
                new() { Key = "attendees", Label = "参会人员", Type = "string", Description = "参会人员列表" },
                new() { Key = "absent", Label = "缺席人员", Type = "string", Description = "缺席人员列表" },
                new() { Key = "agenda", Label = "会议议程", Type = "array", Description = "会议议程列表",
                    SubFields = new List<FieldDefinition>
                    {
                        new() { Key = "topic", Label = "议题", Type = "string" },
                        new() { Key = "discussion", Label = "讨论内容", Type = "string" },
                        new() { Key = "speaker", Label = "发言人", Type = "string" }
                    }
                },
                new() { Key = "decisions", Label = "决策事项", Type = "array", Description = "会议决策列表",
                    SubFields = new List<FieldDefinition>
                    {
                        new() { Key = "decision", Label = "决策内容", Type = "string" },
                        new() { Key = "responsible_person", Label = "责任人", Type = "string" },
                        new() { Key = "deadline", Label = "截止日期", Type = "string" }
                    }
                },
                new() { Key = "action_items", Label = "待办事项", Type = "array", Description = "待办事项列表",
                    SubFields = new List<FieldDefinition>
                    {
                        new() { Key = "task", Label = "任务描述", Type = "string" },
                        new() { Key = "assignee", Label = "负责人", Type = "string" },
                        new() { Key = "priority", Label = "优先级", Type = "string" },
                        new() { Key = "due_date", Label = "完成期限", Type = "string" }
                    }
                },
                new() { Key = "next_meeting", Label = "下次会议时间", Type = "string", Description = "下次会议安排" },
                new() { Key = "next_topics", Label = "下次议题", Type = "string", Description = "下次会议议题预告" },
            },
            ExampleJson = @"{""meeting_title"":""项目启动会"",""meeting_time"":""2024-01-15 14:00-16:00"",""attendees"":""张三、李四、王五""}"
        },

        // 11. 通用提取（兜底模板）
        new DocumentTypeTemplate
        {
            Id = "general",
            Name = "通用提取",
            Icon = "📋",
            Fields = new List<FieldDefinition>
            {
                new() { Key = "document_title", Label = "文档标题", Type = "string", Description = "文档标题或名称" },
                new() { Key = "document_type", Label = "文档类型", Type = "string", Description = "文档类型描述" },
                new() { Key = "creation_date", Label = "创建日期", Type = "string", Description = "文档创建或发布日期" },
                new() { Key = "author", Label = "作者", Type = "string", Description = "文档作者或创建人" },
                new() { Key = "summary", Label = "主要内容摘要", Type = "string", Description = "文档主要内容的简要概括" },
                new() { Key = "key_points", Label = "关键信息点", Type = "string", Description = "文档中的关键信息和要点" },
                new() { Key = "people", Label = "相关人物", Type = "string", Description = "文档中提到的重要人物" },
                new() { Key = "locations", Label = "相关地点", Type = "string", Description = "文档中提到的地点" },
                new() { Key = "organizations", Label = "相关机构", Type = "string", Description = "文档中提到的组织机构" },
                new() { Key = "time_info", Label = "时间信息", Type = "string", Description = "文档中的重要时间点" },
                new() { Key = "amounts", Label = "金额数据", Type = "string", Description = "文档中的金额和数字" },
                new() { Key = "key_data", Label = "关键数据", Type = "string", Description = "重要的统计数据和指标" },
                new() { Key = "action_items", Label = "行动项", Type = "string", Description = "需要采取的行动或任务" },
                new() { Key = "responsible_persons", Label = "责任人", Type = "string", Description = "负责人信息" },
                new() { Key = "deadlines", Label = "时间节点", Type = "string", Description = "重要的截止日期和时间节点" },
            },
            ExampleJson = @"{""document_title"":""文档标题"",""summary"":""主要内容..."",""key_points"":""关键信息...""}"
        },
    };

    /// <summary>
    /// 根据ID获取模板，如果不存在返回通用模板
    /// </summary>
    public static DocumentTypeTemplate GetTemplate(string id)
    {
        return AllTemplates.FirstOrDefault(t => t.Id == id)
            ?? AllTemplates.First(t => t.Id == "general");
    }

    /// <summary>
    /// 获取所有模板的简要信息（用于下拉菜单）
    /// </summary>
    public static List<(string Id, string DisplayName)> GetAllTemplateNames()
    {
        return AllTemplates.Select(t => (t.Id, $"{t.Icon} {t.Name}")).ToList();
    }
}
