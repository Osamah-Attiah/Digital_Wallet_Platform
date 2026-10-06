# Digital Wallet Platform

منصة محفظة رقمية تجريبية بـ.NET 10 وPostgreSQL وReact Admin. تستخدم بيانات وأرصدة اصطناعية فقط. تحافظ العمليات المالية على Ledger مزدوج القيد وIdempotency وAtomic Commit وOutbox/Inbox، وتفصل Maker عن Checker وتدقق التغييرات الإدارية.

الحالة الحالية: **المراحل 1–13 موثقة؛ المرحلة 14 في Cloud Readiness، والنشر السحابي ينتظر موافقة المستخدم**. لا يوجد ادعاء امتثال SAMA أو PCI أو ISO دون تدقيق رسمي.

## تشغيل محلي

يلزم Docker Compose و.NET SDK المحدد في `global.json` وNode المحدد في `.nvmrc`:

```sh
python3 ops/scripts/init-local.py
python3 ops/scripts/release.py
docker compose --profile observability build
docker compose --profile observability up -d --wait --wait-timeout 240
```

Admin: `http://localhost:8081`، API: `http://localhost:8080`. الأسرار المحلية تُولد في `.local/secrets` المهملة من Git. اقرأ [دليل المرحلة 13](docs/deployment/local-compose.md) قبل تشغيل الاختبارات أو سيناريوهات fault؛ local fixtures مخصصة للتطوير ولا تظهر في عقد Cloud Production.

```sh
dotnet restore --locked-mode
dotnet build Digital_Wallet_Platform.sln -c Release --no-restore -warnaserror
dotnet test Digital_Wallet_Platform.sln -c Release --no-build
npm ci --ignore-scripts --prefix apps/admin
npm test --prefix apps/admin -- --run
npm run build --prefix apps/admin
WALLET_EVIDENCE_DIR=ops/validation/phase14 python3 ops/scripts/migration-tests.py
WALLET_EVIDENCE_DIR=ops/validation/phase14 python3 ops/scripts/smoke.py
WALLET_EVIDENCE_DIR=ops/validation/phase14 python3 ops/scripts/scan.py
```

## الوثائق والأدلة

- [تحليل النظام وخارطة المراحل](docs/system-analysis-and-architecture.ar.md).
- [البنية ومخطط النشر السحابي](docs/architecture/cloud-demo-phase14.md).
- [التشغيل والنشر وrollback وBackup/Restore](docs/deployment/cloud/operations.md).
- [Demo Script](docs/deployment/cloud/demo-script.md).
- [التكلفة والميزانية وteardown](docs/deployment/cloud/cost-teardown.md)، و[سجل التكلفة](ops/validation/phase14/cost-ledger.csv).
- [إصلاحات الصور وlibgssapi](docs/deployment/cloud/security-evidence.md).
- [تقرير قبول المرحلة 14 وحدوده](ops/validation/phase14/README.md)، و[قبول المرحلة 13 التاريخي](ops/validation/phase13/README.md).

## Cloud Readiness

الاقتراح: GCP مع VM واحدة `e2-standard-2` وقرص بيانات مستقل وشبكات خاصة وTLS وSecret Manager وIAM محددة وPrometheus/Grafana/Alertmanager. Terraform مثبت وقابل للتكرار؛ [خطة المراجعة](ops/validation/phase14/iac-plan.txt) تستخدم هويات وهمية وstate فارغة ولا تنشئ موارد. يجب إعداد plan حي بعد الموافقة على المشروع والمنطقة والميزانية، ومراجعة فحص OS والحصص والهوية.

CI/CD يدوي عبر بيئات محمية وOIDC وdigests موقعة وscan attestations؛ لا تنشر عند High/Critical أو image secrets. YAML لا تنشئ حماية environments بذاتها. لا push أو إنشاء repository أو apply أو domain binding أو تنبيه خارجي قبل الإذن الصريح. اختبارات Cloud تبقى Skipped حتى تنفيذها فعليًا؛ نجاح Docker المحلي لا يثبتها.
