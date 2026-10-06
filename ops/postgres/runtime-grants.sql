-- Run as wallet_migrator only, under the global migration lock. No runtime role owns a schema/table.
DO $$ DECLARE s text; BEGIN
  FOREACH s IN ARRAY ARRAY['identity','customers','ledger','wallets','beneficiaries','risk_limits','transfers','transactions','messaging','notifications','audit','administration','reporting'] LOOP
    EXECUTE format('REVOKE ALL ON SCHEMA %I FROM PUBLIC', s);
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO wallet_api, wallet_worker', s);
    EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA %I FROM wallet_api, wallet_worker', s);
    EXECUTE format('GRANT SELECT ON ALL TABLES IN SCHEMA %I TO wallet_api, wallet_worker', s);
    EXECUTE format('REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA %I FROM PUBLIC', s);
    EXECUTE format('GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA %I TO wallet_api, wallet_worker', s);
    EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO wallet_api, wallet_worker', s);
  END LOOP;
  FOREACH s IN ARRAY ARRAY['identity','customers','wallets','beneficiaries','risk_limits','transfers','transactions','administration','reporting','notifications'] LOOP
    EXECUTE format('GRANT INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO wallet_api', s);
    EXECUTE format('REVOKE INSERT, UPDATE, DELETE ON %I."__EFMigrationsHistory" FROM wallet_api', s);
  END LOOP;
END $$;
GRANT SELECT, INSERT ON audit.logs TO wallet_api, wallet_worker;
GRANT INSERT ON messaging.outbox TO wallet_api;
GRANT INSERT, UPDATE ON messaging.outbox TO wallet_worker;
GRANT INSERT, UPDATE, DELETE ON notifications.inbox, notifications.notifications, notifications.preferences TO wallet_worker;
GRANT INSERT, UPDATE ON reporting.jobs, reporting.inbox TO wallet_worker;
GRANT UPDATE ON wallets.wallets TO wallet_worker;
-- Existing invoker-rights balance triggers require these rights, and validate the journal posting boundary.
GRANT INSERT, UPDATE ON ledger.accounts, ledger.journals, ledger.entries, ledger.account_balances TO wallet_api;
GRANT INSERT, UPDATE ON ledger.accounts, ledger.account_balances TO wallet_worker;
-- No CREATE, TRUNCATE, trigger ownership, role membership, or write access to migration history.
