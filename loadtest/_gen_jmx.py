import os
from xml.sax.saxutils import escape

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "jmeter")
os.makedirs(OUT, exist_ok=True)

ALNUM = "abcdefghijklmnopqrstuvwxyz0123456789"
HEX = "abcdef0123456789"


def q(s):
    return escape(s, {'"': "&quot;"})


class Node:
    def __init__(self, xml, children=None):
        self.xml = xml
        self.children = children or []

    def render(self):
        inner = "".join(c.render() for c in self.children)
        return self.xml + "<hashTree>" + inner + "</hashTree>"


def leaf(xml):
    return Node(xml)


def sp(name, value):
    return '<stringProp name="%s">%s</stringProp>' % (q(name), escape(value))


def bp(name, value):
    return '<boolProp name="%s">%s</boolProp>' % (name, "true" if value else "false")


def ip(name, value):
    return '<intProp name="%s">%s</intProp>' % (name, value)


# ---------------------------------------------------------------- elements
def header_manager(name, headers):
    items = "".join(
        '<elementProp name="" elementType="Header">%s%s</elementProp>' % (sp("Header.name", k), sp("Header.value", v))
        for k, v in headers
    )
    return leaf(
        '<HeaderManager guiclass="HeaderPanel" testclass="HeaderManager" testname="%s" enabled="true">'
        '<collectionProp name="HeaderManager.headers">%s</collectionProp></HeaderManager>' % (q(name), items)
    )


def http(name, method, path, body=None, children=None):
    if body is not None:
        args = (
            '<elementProp name="HTTPsampler.Arguments" elementType="Arguments" guiclass="HTTPArgumentsPanel" '
            'testclass="Arguments" testname="User Defined Variables" enabled="true"><collectionProp name="Arguments.arguments">'
            '<elementProp name="" elementType="HTTPArgument">%s%s%s</elementProp></collectionProp></elementProp>'
            % (bp("HTTPArgument.always_encode", False), sp("Argument.value", body), sp("Argument.metadata", "="))
        )
        raw = bp("HTTPSampler.postBodyRaw", True)
    else:
        args = (
            '<elementProp name="HTTPsampler.Arguments" elementType="Arguments" guiclass="HTTPArgumentsPanel" '
            'testclass="Arguments" testname="User Defined Variables" enabled="true"><collectionProp name="Arguments.arguments"/></elementProp>'
        )
        raw = ""
    xml = (
        '<HTTPSamplerProxy guiclass="HttpTestSampleGui" testclass="HTTPSamplerProxy" testname="%s" enabled="true">%s%s%s%s%s%s%s%s%s%s</HTTPSamplerProxy>'
        % (
            q(name),
            raw,
            args,
            sp("HTTPSampler.domain", "${HOST}"),
            sp("HTTPSampler.port", "${PORT}"),
            sp("HTTPSampler.protocol", "${PROTOCOL}"),
            sp("HTTPSampler.path", path),
            sp("HTTPSampler.method", method),
            bp("HTTPSampler.follow_redirects", True),
            bp("HTTPSampler.use_keepalive", True),
            bp("HTTPSampler.DO_MULTIPART_POST", False),
        )
    )
    return Node(xml, children)


def status_assert(codes, label=None):
    codes = list(codes)
    strings = "".join(sp(c, c) for c in codes)
    test_type = 8 if len(codes) == 1 else 40  # 8 = Equals, +32 = Or
    label = label or ("Status is " + " or ".join(codes))
    return leaf(
        '<ResponseAssertion guiclass="AssertionGui" testclass="ResponseAssertion" testname="%s" enabled="true">'
        '<collectionProp name="Asserion.test_strings">%s</collectionProp>%s%s%s%s</ResponseAssertion>'
        % (
            q(label),
            strings,
            sp("Assertion.custom_message", ""),
            sp("Assertion.test_field", "Assertion.response_code"),
            bp("Assertion.assume_success", False),
            ip("Assertion.test_type", test_type),
        )
    )


def json_extract(name, refs, paths, default="NOT_FOUND"):
    n = len(refs.split(";"))
    return leaf(
        '<JSONPostProcessor guiclass="JSONPostProcessorGui" testclass="JSONPostProcessor" testname="%s" enabled="true">%s%s%s%s</JSONPostProcessor>'
        % (
            q(name),
            sp("JSONPostProcessor.referenceNames", refs),
            sp("JSONPostProcessor.jsonPathExprs", paths),
            sp("JSONPostProcessor.match_numbers", ";".join(["1"] * n)),
            sp("JSONPostProcessor.defaultValues", ";".join([default] * n)),
        )
    )


def groovy(kind, name, script):
    return leaf(
        '<%s guiclass="TestBeanGUI" testclass="%s" testname="%s" enabled="true">%s%s%s%s%s</%s>'
        % (
            kind,
            kind,
            q(name),
            sp("cacheKey", "true"),
            sp("filename", ""),
            sp("parameters", ""),
            sp("script", script),
            sp("scriptLanguage", "groovy"),
            kind,
        )
    )


def user_params(name, pairs):
    names = "".join(sp("n%d" % i, k) for i, (k, _) in enumerate(pairs))
    vals = "".join(sp("v%d" % i, v) for i, (_, v) in enumerate(pairs))
    return leaf(
        '<UserParameters guiclass="UserParametersGui" testclass="UserParameters" testname="%s" enabled="true">'
        '<collectionProp name="UserParameters.names">%s</collectionProp>'
        '<collectionProp name="UserParameters.thread_values"><collectionProp name="thread_values_0">%s</collectionProp></collectionProp>'
        '%s</UserParameters>' % (q(name), names, vals, bp("UserParameters.per_iteration", True))
    )


def pause(name, ms_expr):
    return leaf(
        '<TestAction guiclass="TestActionGui" testclass="TestAction" testname="%s" enabled="true">%s%s%s%s</TestAction>'
        % (q(name), ip("ActionProcessor.action", 1), ip("ActionProcessor.target", 0), sp("ActionProcessor.duration", ms_expr), sp("TestPlan.comments", ""))
    )


def think_timer():
    return leaf(
        '<ConstantTimer guiclass="ConstantTimerGui" testclass="ConstantTimer" testname="Think time (-Jthink_ms)" enabled="true">%s</ConstantTimer>'
        % sp("ConstantTimer.delay", "${__P(think_ms,0)}")
    )


def once_only(name, children):
    return Node(
        '<OnceOnlyController guiclass="OnceOnlyControllerGui" testclass="OnceOnlyController" testname="%s" enabled="true"/>' % q(name),
        children,
    )


def if_prop(name, prop, children):
    return Node(
        '<IfController guiclass="IfControllerPanel" testclass="IfController" testname="%s" enabled="true">%s%s%s</IfController>'
        % (
            q(name),
            sp("IfController.condition", '${__groovy(props.getProperty("%s","false") == "true")}' % prop),
            bp("IfController.evaluateAll", False),
            bp("IfController.useExpression", True),
        ),
        children,
    )


def pct(name, prop, default, children):
    return Node(
        '<ThroughputController guiclass="ThroughputControllerGui" testclass="ThroughputController" testname="%s" enabled="true">%s%s%s%s</ThroughputController>'
        % (
            q(name),
            ip("ThroughputController.style", 1),
            bp("ThroughputController.perThread", False),
            ip("ThroughputController.maxThroughput", 1),
            sp("ThroughputController.percentThroughput", "${__P(%s,%s)}" % (prop, default)),
        ),
        children,
    )


def summary():
    return leaf(
        '<ResultCollector guiclass="SummaryReport" testclass="ResultCollector" testname="Summary Report" enabled="true">'
        '<boolProp name="ResultCollector.error_logging">false</boolProp><objProp><name>saveConfig</name>'
        '<value class="SampleSaveConfiguration"><time>true</time><latency>true</latency><timestamp>true</timestamp><success>true</success>'
        '<label>true</label><code>true</code><message>true</message><threadName>true</threadName><dataType>true</dataType>'
        '<encoding>false</encoding><assertions>true</assertions><subresults>true</subresults><responseData>false</responseData>'
        '<samplerData>false</samplerData><xml>false</xml><fieldNames>true</fieldNames><responseHeaders>false</responseHeaders>'
        '<requestHeaders>false</requestHeaders><responseDataOnError>false</responseDataOnError>'
        '<saveAssertionResultsFailureMessage>true</saveAssertionResultsFailureMessage><assertionsResultsToSave>0</assertionsResultsToSave>'
        '<bytes>true</bytes><sentBytes>true</sentBytes><url>true</url><threadCounts>true</threadCounts><idleTime>true</idleTime>'
        '<connectTime>true</connectTime></value></objProp><stringProp name="filename"></stringProp></ResultCollector>'
    )


def thread_group(name, children, threads="${__P(threads,10)}", loops="${__P(loops,5)}", setup=False):
    gui, cls = ("SetupThreadGroupGui", "SetupThreadGroup") if setup else ("ThreadGroupGui", "ThreadGroup")
    xml = (
        '<%s guiclass="%s" testclass="%s" testname="%s" enabled="true">%s'
        '<elementProp name="ThreadGroup.main_controller" elementType="LoopController" guiclass="LoopControlPanel" testclass="LoopController" testname="Loop Controller" enabled="true">%s%s</elementProp>'
        '%s%s%s%s%s%s</%s>'
        % (
            cls,
            gui,
            cls,
            q(name),
            sp("ThreadGroup.on_sample_error", "continue"),
            bp("LoopController.continue_forever", False),
            sp("LoopController.loops", loops),
            sp("ThreadGroup.num_threads", threads),
            sp("ThreadGroup.ramp_time", "${__P(rampup,30)}"),
            bp("ThreadGroup.scheduler", False),
            sp("ThreadGroup.duration", ""),
            sp("ThreadGroup.delay", ""),
            bp("ThreadGroup.same_user_on_next_iteration", True),
            cls,
        )
    )
    return Node(xml, children)


def test_plan(name, comment, extra_vars, groups):
    base = [
        ("HOST", "${__P(host,localhost)}"),
        ("PORT", "${__P(port,5000)}"),
        ("PROTOCOL", "${__P(protocol,http)}"),
    ] + extra_vars
    args = "".join(
        '<elementProp name="%s" elementType="Argument">%s%s%s</elementProp>'
        % (k, sp("Argument.name", k), sp("Argument.value", v), sp("Argument.metadata", "="))
        for k, v in base
    )
    xml = (
        '<TestPlan guiclass="TestPlanGui" testclass="TestPlan" testname="%s" enabled="true">%s%s%s'
        '<elementProp name="TestPlan.user_defined_variables" elementType="Arguments" guiclass="ArgumentsPanel" testclass="Arguments" testname="User Defined Variables" enabled="true">'
        '<collectionProp name="Arguments.arguments">%s</collectionProp></elementProp>%s</TestPlan>'
        % (
            q(name),
            sp("TestPlan.comments", comment),
            bp("TestPlan.functional_mode", False),
            bp("TestPlan.serialize_threadgroups", False),
            args,
            sp("TestPlan.user_define_classpath", ""),
        )
    )
    root = Node(xml, groups + [summary()])
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n<jmeterTestPlan version="1.2" properties="5.0" jmeter="5.6.3"><hashTree>'
        + root.render()
        + "</hashTree></jmeterTestPlan>\n"
    )


def save(fname, content):
    with open(os.path.join(OUT, fname), "w", encoding="utf-8", newline="\n") as f:
        f.write(content)
    print("wrote", fname)


# ---------------------------------------------------------------- shared samplers
JSON_HDR = header_manager("Content-Type: application/json", [("Content-Type", "application/json"), ("Accept", "application/json")])


def bearer(token_var):
    return header_manager("Authorization: Bearer", [("Authorization", "Bearer ${%s}" % token_var)])


def login(name="POST /auth/login", token="token"):
    return http(
        name,
        "POST",
        "/api/v1/auth/login",
        body='{"userId":"${__UUID()}","role":"Customer"}',
        children=[
            status_assert(["200"]),
            json_extract("Extract accessToken", token, "$.data.accessToken"),
        ],
    )


def create_wallet(token="token", wid="walletId", acct="accountNumber", name="POST /wallets (create)"):
    return http(
        name,
        "POST",
        "/api/v1/wallets",
        children=[
            bearer(token),
            status_assert(["200"]),
            json_extract("Extract walletId + accountNumber", "%s;%s" % (wid, acct), "$.data.walletId;$.data.accountNumber"),
        ],
    )


def credit_body(acct_var, kobo, sess, ref):
    return (
        '{"sessionId":"%s","transactionReference":"%s","amountKobo":%s,'
        '"beneficiaryAccountNumber":"${%s}","originatingAccountNumber":"0123456789",'
        '"originatingBankCode":"058","narration":"JMeter load test credit"}' % (sess, ref, kobo, acct_var)
    )


def rand_session():
    return "S${__RandomString(20,%s,)}" % ALNUM


def credit_setup(acct_var, name="POST /wallets/credit (fund wallet)", children=None):
    return http(
        name,
        "POST",
        "/api/v1/wallets/credit",
        body=credit_body(acct_var, "${CREDIT_KOBO}", rand_session(), "TX-${__UUID()}"),
        children=[status_assert(["202"])] + (children or []),
    )


COMMON_VARS = [("CREDIT_KOBO", "${__P(credit_kobo,100000000)}"), ("SETTLE_WAIT_MS", "${__P(settle_wait_ms,10000)}")]
SETTLE = "Wait for deposit consumer (-Jsettle_wait_ms)"

# ---------------------------------------------------------------- 1. wallet creation
save(
    "wallet-creation.jmx",
    test_plan(
        "NovaWallet - Wallet Creation",
        "Each iteration logs in as a brand-new user (mock auth) and creates their wallet. Override with -Jthreads -Jrampup -Jloops -Jhost -Jport -Jthink_ms.",
        [],
        [thread_group("Create wallets", [JSON_HDR, think_timer(), login(), create_wallet()])],
    ),
)

# ---------------------------------------------------------------- 2. statement
save(
    "wallet-statement.jmx",
    test_plan(
        "NovaWallet - Statement",
        "Per thread: login, create wallet and fund it once (Once Only), then loop GET /wallets/statement with and without a date range.",
        COMMON_VARS,
        [
            thread_group(
                "Statement readers",
                [
                    JSON_HDR,
                    once_only("Per-thread setup", [login(), create_wallet(), credit_setup("accountNumber"), pause(SETTLE, "${SETTLE_WAIT_MS}")]),
                    bearer("token"),
                    think_timer(),
                    http("GET /wallets/statement (page 1)", "GET", "/api/v1/wallets/statement?pageNumber=1&pageSize=20", children=[status_assert(["200"])]),
                    http(
                        "GET /wallets/statement (date range)",
                        "GET",
                        "/api/v1/wallets/statement?pageNumber=1&pageSize=50&fromDate=${__timeShift(yyyy-MM-dd,,-P7D,)}&toDate=${__timeShift(yyyy-MM-dd,,P1D,)}",
                        children=[status_assert(["200"])],
                    ),
                ],
            )
        ],
    ),
)

# ---------------------------------------------------------------- 3. credit
sess = "${sessionId}"
ref = "${txRef}"
save(
    "wallet-credit.jmx",
    test_plan(
        "NovaWallet - Wallet Credit",
        "Anonymous NIP credit webhook (POST /api/v1/wallets/credit) against a per-thread wallet. Each request carries a W3C traceparent. Use -Jreplay=true to also resend the same SessionId and expect 202.",
        [("AMOUNT_KOBO", "${__P(amount_kobo,1000)}")] + COMMON_VARS,
        [
            thread_group(
                "Credit webhooks",
                [
                    JSON_HDR,
                    once_only("Per-thread setup", [login(), create_wallet()]),
                    think_timer(),
                    user_params(
                        "Per-iteration ids",
                        [
                            ("sessionId", rand_session()),
                            ("txRef", "TX-${__UUID()}"),
                            ("traceId", "${__RandomString(32,%s,)}" % HEX),
                            ("spanId", "${__RandomString(16,%s,)}" % HEX),
                        ],
                    ),
                    header_manager("traceparent (search OpenObserve for the traceId var)", [("traceparent", "00-${traceId}-${spanId}-01")]),
                    http(
                        "POST /wallets/credit",
                        "POST",
                        "/api/v1/wallets/credit",
                        body=credit_body("accountNumber", "${AMOUNT_KOBO}", sess, ref),
                        children=[status_assert(["202"])],
                    ),
                    if_prop(
                        "Replay same SessionId? (-Jreplay=true)",
                        "replay",
                        [
                            http(
                                "POST /wallets/credit (replay)",
                                "POST",
                                "/api/v1/wallets/credit",
                                body=credit_body("accountNumber", "${AMOUNT_KOBO}", sess, ref),
                                children=[status_assert(["202"])],
                            )
                        ],
                    ),
                ],
            )
        ],
    ),
)

# ---------------------------------------------------------------- 4. transfer
transfer_body = (
    '{"sourceWalletId":"${walletIdA}","destinationWalletId":"${walletIdB}",'
    '"amountInKobo":${TRANSFER_KOBO},"narration":"JMeter load test transfer"}'
)
save(
    "wallet-transfer.jmx",
    test_plan(
        "NovaWallet - Wallet Transfer",
        "Per thread: sender A (funded) and receiver B are created once, then A transfers to B in a loop. The per-user limit is 10 transfers/min, so 429 is accepted alongside 200.",
        COMMON_VARS + [("TRANSFER_KOBO", "${__P(transfer_kobo,100)}")],
        [
            thread_group(
                "Transfers (one sender per thread)",
                [
                    JSON_HDR,
                    once_only(
                        "Per-thread setup",
                        [
                            login("POST /auth/login (sender A)", "tokenA"),
                            create_wallet("tokenA", "walletIdA", "accountNumberA", "POST /wallets (sender A)"),
                            login("POST /auth/login (receiver B)", "tokenB"),
                            create_wallet("tokenB", "walletIdB", "accountNumberB", "POST /wallets (receiver B)"),
                            credit_setup("accountNumberA", "POST /wallets/credit (fund sender A)"),
                            pause(SETTLE, "${SETTLE_WAIT_MS}"),
                        ],
                    ),
                    think_timer(),
                    http(
                        "POST /wallets/transfer",
                        "POST",
                        "/api/v1/wallets/transfer",
                        body=transfer_body,
                        children=[
                            bearer("tokenA"),
                            header_manager("Idempotency-Key", [("Idempotency-Key", "${__UUID()}")]),
                            status_assert(["200", "429"], "Status is 200 (ok) or 429 (rate limited)"),
                        ],
                    ),
                ],
            )
        ],
    ),
)

# ---------------------------------------------------------------- 5. concurrent multi-wallet
SETUP_STORE = """int n = ctx.getThreadNum() + 1
props.put("w" + n + "_token", vars.get("token") ?: "")
props.put("w" + n + "_walletId", vars.get("walletId") ?: "")
props.put("w" + n + "_account", vars.get("accountNumber") ?: "")
props.put("wallet_count", String.valueOf(ctx.getThreadGroup().getNumThreads()))
"""

PICK_ONE = """int n = Integer.parseInt(props.getProperty("wallet_count"))
int i = java.util.concurrent.ThreadLocalRandom.current().nextInt(n) + 1
vars.put("sel_token", props.getProperty("w" + i + "_token"))
vars.put("sel_account", props.getProperty("w" + i + "_account"))
"""

PICK_PAIR = """int n = Integer.parseInt(props.getProperty("wallet_count"))
def rnd = java.util.concurrent.ThreadLocalRandom.current()
int s = rnd.nextInt(n) + 1
int d = rnd.nextInt(n - 1) + 1
if (d >= s) { d++ }
vars.put("src_token", props.getProperty("w" + s + "_token"))
vars.put("src_wallet", props.getProperty("w" + s + "_walletId"))
vars.put("dst_wallet", props.getProperty("w" + d + "_walletId"))
"""

conc_credit_body = credit_body("sel_account", "${AMOUNT_KOBO}", rand_session(), "TX-${__UUID()}")
conc_transfer_body = (
    '{"sourceWalletId":"${src_wallet}","destinationWalletId":"${dst_wallet}",'
    '"amountInKobo":${TRANSFER_KOBO},"narration":"JMeter concurrent transfer"}'
)

save(
    "wallet-concurrent-multiwallet.jmx",
    test_plan(
        "NovaWallet - Concurrent Multi-Wallet",
        "setUp group creates and funds N wallets (-Jwallets, default 10, minimum 2) and stores them as JMeter properties. "
        "Concurrent users then run a weighted mix (-Jcredit_pct/-Jstatement_pct/-Jtransfer_pct) against random wallets. "
        "Transfers may return 200, 422 (insufficient funds) or 429 (per-user rate limit); anything else, including 5xx, fails.",
        COMMON_VARS + [("AMOUNT_KOBO", "${__P(amount_kobo,1000)}"), ("TRANSFER_KOBO", "${__P(transfer_kobo,100)}")],
        [
            thread_group(
                "setUp - create and fund wallets",
                [
                    JSON_HDR,
                    login(),
                    create_wallet(),
                    credit_setup(
                        "accountNumber",
                        "POST /wallets/credit (fund wallet)",
                        [groovy("JSR223PostProcessor", "Store wallet in JMeter properties", SETUP_STORE)],
                    ),
                    pause(SETTLE, "${SETTLE_WAIT_MS}"),
                ],
                threads="${__P(wallets,10)}",
                loops="1",
                setup=True,
            ),
            thread_group(
                "Concurrent users",
                [
                    JSON_HDR,
                    think_timer(),
                    pct(
                        "Credit mix (-Jcredit_pct)",
                        "credit_pct",
                        40,
                        [
                            groovy("JSR223PreProcessor", "Pick random wallet", PICK_ONE),
                            http("POST /wallets/credit", "POST", "/api/v1/wallets/credit", body=conc_credit_body, children=[status_assert(["202"])]),
                        ],
                    ),
                    pct(
                        "Statement mix (-Jstatement_pct)",
                        "statement_pct",
                        40,
                        [
                            groovy("JSR223PreProcessor", "Pick random wallet", PICK_ONE),
                            bearer("sel_token"),
                            http("GET /wallets/statement", "GET", "/api/v1/wallets/statement?pageNumber=1&pageSize=20", children=[status_assert(["200"])]),
                        ],
                    ),
                    pct(
                        "Transfer mix (-Jtransfer_pct)",
                        "transfer_pct",
                        20,
                        [
                            groovy("JSR223PreProcessor", "Pick random source and destination", PICK_PAIR),
                            bearer("src_token"),
                            header_manager("Idempotency-Key", [("Idempotency-Key", "${__UUID()}")]),
                            http(
                                "POST /wallets/transfer",
                                "POST",
                                "/api/v1/wallets/transfer",
                                body=conc_transfer_body,
                                children=[status_assert(["200", "422", "429"], "Status is 200, 422 or 429 (never 5xx)")],
                            ),
                        ],
                    ),
                ],
                threads="${__P(threads,10)}",
            ),
        ],
    ),
)
