-- The migration owner installs these functions. Runtime roles must not own this schema,
-- tables or functions, have TRUNCATE/DDL privileges, or be able to disable triggers.
CREATE FUNCTION ledger.reject_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Financial history is immutable' USING ERRCODE = '23514', CONSTRAINT = 'ledger_immutable';
END $$;

CREATE FUNCTION ledger.initialize_balance() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO ledger.account_balances ("AccountId", "BalanceMinor", "Version", "UpdatedAt")
    VALUES (NEW."Id", 0, 0, clock_timestamp());
    RETURN NEW;
END $$;
CREATE TRIGGER account_initialize AFTER INSERT ON ledger.accounts
    FOR EACH ROW EXECUTE FUNCTION ledger.initialize_balance();
CREATE TRIGGER account_immutable BEFORE UPDATE OR DELETE ON ledger.accounts
    FOR EACH ROW EXECUTE FUNCTION ledger.reject_mutation();

CREATE FUNCTION ledger.guard_balance() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    -- Only account initialization and journal approval may write the projection.
    IF TG_OP = 'DELETE' OR pg_trigger_depth() < 2 THEN
        RAISE EXCEPTION 'Balance is maintained by posting' USING ERRCODE = '23514', CONSTRAINT = 'ledger_projection_owned';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER balance_owned BEFORE INSERT OR UPDATE OR DELETE ON ledger.account_balances
    FOR EACH ROW EXECUTE FUNCTION ledger.guard_balance();

CREATE FUNCTION ledger.guard_entry() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE state text;
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'Entries are append only' USING ERRCODE = '23514', CONSTRAINT = 'ledger_immutable';
    END IF;
    -- Serializes appending with approval, including direct SQL from another connection.
    SELECT "Status" INTO state FROM ledger.journals WHERE "Id" = NEW."JournalId" FOR UPDATE;
    IF state IS DISTINCT FROM 'Draft' THEN
        RAISE EXCEPTION 'Cannot append to a posted journal' USING ERRCODE = '23514', CONSTRAINT = 'ledger_immutable';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER entry_guard BEFORE INSERT OR UPDATE OR DELETE ON ledger.entries
    FOR EACH ROW EXECUTE FUNCTION ledger.guard_entry();

CREATE FUNCTION ledger.approve_journal() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE item record; delta numeric; next_balance numeric; line_count bigint; net numeric;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Journals are append only' USING ERRCODE = '23514', CONSTRAINT = 'ledger_immutable';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 'Draft' OR NEW."PostedAt" IS NOT NULL THEN
            RAISE EXCEPTION 'A journal must start as draft' USING ERRCODE = '23514', CONSTRAINT = 'ledger_posting_boundary';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD."Status" = 'Posted' OR NEW."Status" <> 'Posted'
       OR (to_jsonb(NEW) - 'Status' - 'PostedAt') <> (to_jsonb(OLD) - 'Status' - 'PostedAt') THEN
        RAISE EXCEPTION 'Only draft approval is permitted' USING ERRCODE = '23514', CONSTRAINT = 'ledger_immutable';
    END IF;
    SELECT count(*), coalesce(sum(CASE WHEN "Side" = 'Debit' THEN "AmountMinor"::numeric ELSE -"AmountMinor"::numeric END), 0)
      INTO line_count, net FROM ledger.entries WHERE "JournalId" = NEW."Id";
    IF line_count < 2 OR net <> 0 THEN
        RAISE EXCEPTION 'Journal is not balanced' USING ERRCODE = '23514', CONSTRAINT = 'ledger_balanced';
    END IF;
    -- Composite foreign keys enforce entry = journal = account currency, even without the application.
    IF NEW."ReversalOf" IS NOT NULL THEN
        IF NOT EXISTS (SELECT 1 FROM ledger.journals WHERE "Id" = NEW."ReversalOf" AND "Status" = 'Posted'
                       AND "Currency" = NEW."Currency" AND "ReversalOf" IS NULL)
           OR EXISTS (
               SELECT 1 FROM
                 (SELECT "AccountId", "Side", sum("AmountMinor"::numeric) amount
                  FROM ledger.entries WHERE "JournalId" = NEW."Id" GROUP BY "AccountId", "Side") n
               FULL JOIN
                 (SELECT "AccountId", CASE WHEN "Side" = 'Debit' THEN 'Credit' ELSE 'Debit' END AS "Side",
                         sum("AmountMinor"::numeric) amount
                  FROM ledger.entries WHERE "JournalId" = NEW."ReversalOf" GROUP BY "AccountId", "Side") o
               USING ("AccountId", "Side") WHERE n.amount IS DISTINCT FROM o.amount
           ) THEN
            RAISE EXCEPTION 'Reversal must exactly compensate original' USING ERRCODE = '23514', CONSTRAINT = 'ledger_invalid_reversal';
        END IF;
    END IF;
    NEW."PostedAt" := clock_timestamp();
    -- Lock every affected account in PostgreSQL UUID order, before applying ANY delta.
    PERFORM b."AccountId" FROM ledger.account_balances b
      WHERE b."AccountId" IN (SELECT "AccountId" FROM ledger.entries WHERE "JournalId" = NEW."Id")
      ORDER BY b."AccountId" FOR UPDATE;
    IF (SELECT count(*) FROM ledger.account_balances WHERE "AccountId" IN
            (SELECT "AccountId" FROM ledger.entries WHERE "JournalId" = NEW."Id"))
       <> (SELECT count(DISTINCT "AccountId") FROM ledger.entries WHERE "JournalId" = NEW."Id") THEN
        RAISE EXCEPTION 'Missing projection' USING ERRCODE = '23514', CONSTRAINT = 'ledger_projection_missing';
    END IF;
    FOR item IN SELECT a."Id", a."NormalSide", a."Kind", b."BalanceMinor"
        FROM ledger.accounts a JOIN ledger.account_balances b ON b."AccountId" = a."Id"
        WHERE a."Id" IN (SELECT "AccountId" FROM ledger.entries WHERE "JournalId" = NEW."Id") ORDER BY a."Id"
    LOOP
        SELECT sum(CASE WHEN "Side" = item."NormalSide" THEN "AmountMinor"::numeric ELSE -"AmountMinor"::numeric END)
          INTO delta FROM ledger.entries WHERE "JournalId" = NEW."Id" AND "AccountId" = item."Id";
        next_balance := item."BalanceMinor"::numeric + delta;
        IF item."Kind" = 'CustomerWalletLiability' AND next_balance < 0 THEN
            RAISE EXCEPTION 'Insufficient wallet balance' USING ERRCODE = '23514', CONSTRAINT = 'ledger_insufficient_balance';
        END IF;
        UPDATE ledger.account_balances SET "BalanceMinor" = next_balance::bigint,
            "Version" = "Version" + 1, "UpdatedAt" = NEW."PostedAt" WHERE "AccountId" = item."Id";
    END LOOP;
    RETURN NEW;
END $$;
CREATE TRIGGER journal_approval BEFORE INSERT OR UPDATE OR DELETE ON ledger.journals
    FOR EACH ROW EXECUTE FUNCTION ledger.approve_journal();

CREATE FUNCTION ledger.require_posted() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM ledger.journals WHERE "Id" = NEW."Id" AND "Status" = 'Posted') THEN
        RAISE EXCEPTION 'Draft journals cannot be committed' USING ERRCODE = '23514', CONSTRAINT = 'ledger_posting_boundary';
    END IF;
    RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER journal_commit AFTER INSERT OR UPDATE ON ledger.journals
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ledger.require_posted();

CREATE TRIGGER journals_no_truncate BEFORE TRUNCATE ON ledger.journals FOR EACH STATEMENT EXECUTE FUNCTION ledger.reject_mutation();
CREATE TRIGGER entries_no_truncate BEFORE TRUNCATE ON ledger.entries FOR EACH STATEMENT EXECUTE FUNCTION ledger.reject_mutation();
CREATE TRIGGER accounts_no_truncate BEFORE TRUNCATE ON ledger.accounts FOR EACH STATEMENT EXECUTE FUNCTION ledger.reject_mutation();
CREATE TRIGGER balances_no_truncate BEFORE TRUNCATE ON ledger.account_balances FOR EACH STATEMENT EXECUTE FUNCTION ledger.reject_mutation();
