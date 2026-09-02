\set ON_ERROR_STOP on

DO $$
BEGIN
    IF current_database() <> 'task7_claim_consistency' THEN
        RAISE EXCEPTION
            'Este experimento só pode executar no database descartável task7_claim_consistency; database atual: %.',
            current_database();
    END IF;
END;
$$;

DROP SCHEMA IF EXISTS harness CASCADE;
DROP SCHEMA IF EXISTS attachment CASCADE;
DROP SCHEMA IF EXISTS notifications CASCADE;
DROP SCHEMA IF EXISTS platform CASCADE;
DROP SCHEMA IF EXISTS audit CASCADE;

CREATE SCHEMA attachment;
CREATE SCHEMA notifications;
CREATE SCHEMA platform;
CREATE SCHEMA audit;
CREATE SCHEMA harness;

CREATE TABLE attachment.reservation
(
    run_id text PRIMARY KEY,
    state text NOT NULL CHECK (state IN ('reserved', 'claimed')),
    expires_at timestamptz NOT NULL
);

CREATE TABLE attachment.processed_message
(
    run_id text PRIMARY KEY
);

CREATE TABLE notifications.notification
(
    run_id text PRIMARY KEY
);

CREATE TABLE notifications.idempotency_key
(
    run_id text PRIMARY KEY
);

CREATE TABLE platform.outbox
(
    run_id text NOT NULL,
    kind text NOT NULL CHECK (kind IN ('claim-confirmation', 'notification-accepted')),
    PRIMARY KEY (run_id, kind)
);

CREATE TABLE audit.audit_event
(
    run_id text PRIMARY KEY
);

CREATE TABLE harness.observation
(
    alternative text NOT NULL,
    failure_point text NOT NULL,
    notification_count integer NOT NULL,
    idempotency_count integer NOT NULL,
    acceptance_outbox_count integer NOT NULL,
    confirmation_outbox_count integer NOT NULL,
    audit_count integer NOT NULL,
    reservation_state text,
    processed_count integer NOT NULL,
    PRIMARY KEY (alternative, failure_point)
);

CREATE TABLE harness.sweep_result
(
    name text PRIMARY KEY,
    value integer NOT NULL
);

CREATE OR REPLACE PROCEDURE harness.reserve(
    p_alternative text,
    p_point text,
    p_expired boolean)
LANGUAGE plpgsql
AS $$
DECLARE
    v_run text := p_alternative || ':' || p_point;
BEGIN
    INSERT INTO attachment.reservation (run_id, state, expires_at)
    VALUES
    (
        v_run,
        'reserved',
        CASE
            WHEN p_expired THEN clock_timestamp() - interval '1 second'
            ELSE clock_timestamp() + interval '1 hour'
        END
    );
END;
$$;

CREATE OR REPLACE PROCEDURE harness.observe(
    p_alternative text,
    p_point text)
LANGUAGE plpgsql
AS $$
DECLARE
    v_run text := p_alternative || ':' || p_point;
BEGIN
    INSERT INTO harness.observation
    SELECT
        p_alternative,
        p_point,
        (SELECT count(*)::integer FROM notifications.notification WHERE run_id = v_run),
        (SELECT count(*)::integer FROM notifications.idempotency_key WHERE run_id = v_run),
        (SELECT count(*)::integer FROM platform.outbox
         WHERE run_id = v_run AND kind = 'notification-accepted'),
        (SELECT count(*)::integer FROM platform.outbox
         WHERE run_id = v_run AND kind = 'claim-confirmation'),
        (SELECT count(*)::integer FROM audit.audit_event WHERE run_id = v_run),
        (SELECT state FROM attachment.reservation WHERE run_id = v_run),
        (SELECT count(*)::integer FROM attachment.processed_message WHERE run_id = v_run);
END;
$$;

CREATE OR REPLACE PROCEDURE harness.run_a(p_point text)
LANGUAGE plpgsql
AS $$
DECLARE
    v_run text := 'A:' || p_point;
BEGIN
    BEGIN
        INSERT INTO attachment.reservation
        VALUES (v_run, 'claimed', clock_timestamp() + interval '1 hour');
        IF p_point = 'after_claim' THEN
            RAISE EXCEPTION 'Falha injetada após claim.';
        END IF;

        INSERT INTO notifications.notification VALUES (v_run);
        IF p_point = 'after_notification' THEN
            RAISE EXCEPTION 'Falha injetada após notification.';
        END IF;

        INSERT INTO notifications.idempotency_key VALUES (v_run);
        IF p_point = 'after_idempotency' THEN
            RAISE EXCEPTION 'Falha injetada após idempotency.';
        END IF;

        INSERT INTO platform.outbox VALUES (v_run, 'notification-accepted');
        IF p_point = 'after_accept_outbox' THEN
            RAISE EXCEPTION 'Falha injetada após outbox de aceite.';
        END IF;

        INSERT INTO audit.audit_event VALUES (v_run);
        IF p_point = 'after_audit' THEN
            RAISE EXCEPTION 'Falha injetada após audit.';
        END IF;
    EXCEPTION
        WHEN raise_exception THEN
            NULL;
    END;
END;
$$;

CREATE OR REPLACE PROCEDURE harness.accept_b(p_point text)
LANGUAGE plpgsql
AS $$
DECLARE
    v_run text := 'B:' || p_point;
BEGIN
    BEGIN
        INSERT INTO notifications.notification VALUES (v_run);
        IF p_point = 'after_notification' THEN
            RAISE EXCEPTION 'Falha injetada após notification.';
        END IF;

        INSERT INTO notifications.idempotency_key VALUES (v_run);
        IF p_point = 'after_idempotency' THEN
            RAISE EXCEPTION 'Falha injetada após idempotency.';
        END IF;

        INSERT INTO platform.outbox VALUES (v_run, 'notification-accepted');
        IF p_point = 'after_accept_outbox' THEN
            RAISE EXCEPTION 'Falha injetada após outbox de aceite.';
        END IF;

        INSERT INTO audit.audit_event VALUES (v_run);
        IF p_point = 'after_audit' THEN
            RAISE EXCEPTION 'Falha injetada após audit.';
        END IF;
    EXCEPTION
        WHEN raise_exception THEN
            NULL;
    END;
END;
$$;

CREATE OR REPLACE PROCEDURE harness.accept_c(p_point text)
LANGUAGE plpgsql
AS $$
DECLARE
    v_run text := 'C:' || p_point;
BEGIN
    BEGIN
        INSERT INTO notifications.notification VALUES (v_run);
        IF p_point = 'after_notification' THEN
            RAISE EXCEPTION 'Falha injetada após notification.';
        END IF;

        INSERT INTO notifications.idempotency_key VALUES (v_run);
        IF p_point = 'after_idempotency' THEN
            RAISE EXCEPTION 'Falha injetada após idempotency.';
        END IF;

        INSERT INTO platform.outbox VALUES (v_run, 'claim-confirmation');
        IF p_point = 'after_claim_outbox' THEN
            RAISE EXCEPTION 'Falha injetada após outbox de confirmação.';
        END IF;

        INSERT INTO platform.outbox VALUES (v_run, 'notification-accepted');
        IF p_point = 'after_accept_outbox' THEN
            RAISE EXCEPTION 'Falha injetada após outbox de aceite.';
        END IF;

        INSERT INTO audit.audit_event VALUES (v_run);
        IF p_point = 'after_audit' THEN
            RAISE EXCEPTION 'Falha injetada após audit.';
        END IF;
    EXCEPTION
        WHEN raise_exception THEN
            NULL;
    END;
END;
$$;

CREATE OR REPLACE PROCEDURE harness.consume_c(p_run text)
LANGUAGE plpgsql
AS $$
DECLARE
    v_updated integer;
BEGIN
    UPDATE attachment.reservation
    SET state = 'claimed'
    WHERE run_id = p_run AND state = 'reserved';
    GET DIAGNOSTICS v_updated = ROW_COUNT;

    IF v_updated = 1 THEN
        INSERT INTO attachment.processed_message VALUES (p_run)
        ON CONFLICT DO NOTHING;
    END IF;
END;
$$;

CALL harness.run_a('after_claim');
CALL harness.observe('A', 'after_claim');
CALL harness.run_a('after_notification');
CALL harness.observe('A', 'after_notification');
CALL harness.run_a('after_idempotency');
CALL harness.observe('A', 'after_idempotency');
CALL harness.run_a('after_accept_outbox');
CALL harness.observe('A', 'after_accept_outbox');
CALL harness.run_a('after_audit');
CALL harness.observe('A', 'after_audit');
CALL harness.run_a('after_commit_ack');
CALL harness.observe('A', 'after_commit_ack');

CALL harness.reserve('B', 'after_reservation', true);
CALL harness.observe('B', 'after_reservation');
CALL harness.reserve('B', 'after_notification', true);
CALL harness.accept_b('after_notification');
CALL harness.observe('B', 'after_notification');
CALL harness.reserve('B', 'after_idempotency', true);
CALL harness.accept_b('after_idempotency');
CALL harness.observe('B', 'after_idempotency');
CALL harness.reserve('B', 'after_accept_outbox', true);
CALL harness.accept_b('after_accept_outbox');
CALL harness.observe('B', 'after_accept_outbox');
CALL harness.reserve('B', 'after_audit', true);
CALL harness.accept_b('after_audit');
CALL harness.observe('B', 'after_audit');
CALL harness.reserve('B', 'after_commit_before_confirm', false);
CALL harness.accept_b('after_commit_before_confirm');
CALL harness.observe('B', 'after_commit_before_confirm');
CALL harness.reserve('B', 'after_commit_then_compensate', false);
CALL harness.accept_b('after_commit_then_compensate');
DELETE FROM attachment.reservation WHERE run_id = 'B:after_commit_then_compensate';
CALL harness.observe('B', 'after_commit_then_compensate');
CALL harness.reserve('B', 'success', false);
CALL harness.accept_b('success');
UPDATE attachment.reservation SET state = 'claimed' WHERE run_id = 'B:success';
CALL harness.observe('B', 'success');

CALL harness.reserve('C', 'after_reservation', true);
CALL harness.observe('C', 'after_reservation');
CALL harness.reserve('C', 'after_notification', true);
CALL harness.accept_c('after_notification');
CALL harness.observe('C', 'after_notification');
CALL harness.reserve('C', 'after_idempotency', true);
CALL harness.accept_c('after_idempotency');
CALL harness.observe('C', 'after_idempotency');
CALL harness.reserve('C', 'after_claim_outbox', true);
CALL harness.accept_c('after_claim_outbox');
CALL harness.observe('C', 'after_claim_outbox');
CALL harness.reserve('C', 'after_accept_outbox', true);
CALL harness.accept_c('after_accept_outbox');
CALL harness.observe('C', 'after_accept_outbox');
CALL harness.reserve('C', 'after_audit', true);
CALL harness.accept_c('after_audit');
CALL harness.observe('C', 'after_audit');
CALL harness.reserve('C', 'after_commit_before_consume', false);
CALL harness.accept_c('after_commit_before_consume');
CALL harness.observe('C', 'after_commit_before_consume');
CALL harness.reserve('C', 'confirmation_delayed_past_ttl', true);
CALL harness.accept_c('confirmation_delayed_past_ttl');
CALL harness.observe('C', 'confirmation_delayed_past_ttl');
CALL harness.reserve('C', 'success', false);
CALL harness.accept_c('success');
CALL harness.consume_c('C:success');
CALL harness.consume_c('C:success');
CALL harness.observe('C', 'success');

DO $$
DECLARE
    v_bad integer;
BEGIN
    SELECT count(*) INTO v_bad
    FROM harness.observation
    WHERE alternative = 'A'
      AND failure_point <> 'after_commit_ack'
      AND (notification_count <> 0
           OR idempotency_count <> 0
           OR acceptance_outbox_count <> 0
           OR audit_count <> 0
           OR reservation_state IS NOT NULL);
    IF v_bad <> 0 THEN
        RAISE EXCEPTION 'A não reverteu integralmente em % pontos.', v_bad;
    END IF;

    SELECT count(*) INTO v_bad
    FROM harness.observation
    WHERE alternative = 'A'
      AND failure_point = 'after_commit_ack'
      AND NOT (notification_count = 1
               AND idempotency_count = 1
               AND acceptance_outbox_count = 1
               AND audit_count = 1
               AND reservation_state = 'claimed');
    IF v_bad <> 0 THEN
        RAISE EXCEPTION 'A não preservou o estado integral após commit.';
    END IF;

    SELECT count(*) INTO v_bad
    FROM harness.observation
    WHERE alternative = 'B'
      AND failure_point IN ('after_commit_before_confirm', 'after_commit_then_compensate')
      AND notification_count = 1
      AND idempotency_count = 1
      AND acceptance_outbox_count = 1
      AND confirmation_outbox_count = 0
      AND audit_count = 1
      AND reservation_state IS DISTINCT FROM 'claimed';
    IF v_bad <> 2 THEN
        RAISE EXCEPTION 'B não reproduziu as duas violações esperadas; contagem: %.', v_bad;
    END IF;

    SELECT count(*) INTO v_bad
    FROM harness.observation
    WHERE alternative = 'C'
      AND failure_point = 'after_commit_before_consume'
      AND notification_count = 1
      AND idempotency_count = 1
      AND acceptance_outbox_count = 1
      AND confirmation_outbox_count = 1
      AND audit_count = 1
      AND reservation_state = 'reserved';
    IF v_bad <> 1 THEN
        RAISE EXCEPTION 'C não preservou confirmação durável antes do consumo.';
    END IF;

    SELECT count(*) INTO v_bad
    FROM harness.observation
    WHERE alternative = 'C'
      AND failure_point = 'success'
      AND reservation_state = 'claimed'
      AND processed_count = 1;
    IF v_bad <> 1 THEN
        RAISE EXCEPTION 'O consumo idempotente de C não convergiu para uma marca.';
    END IF;

    SELECT count(*) INTO v_bad FROM harness.observation;
    IF v_bad <> 23 THEN
        RAISE EXCEPTION 'A matriz deveria conter 23 observações; recebeu %.', v_bad;
    END IF;
END;
$$;

WITH swept AS
(
    DELETE FROM attachment.reservation
    WHERE state = 'reserved' AND expires_at <= clock_timestamp()
    RETURNING run_id
)
INSERT INTO harness.sweep_result
SELECT 'swept_count', count(*)::integer FROM swept;

WITH late_confirmation AS
(
    UPDATE attachment.reservation
    SET state = 'claimed'
    WHERE run_id = 'C:confirmation_delayed_past_ttl' AND state = 'reserved'
    RETURNING run_id
)
INSERT INTO harness.sweep_result
SELECT 'late_confirmation_updated', count(*)::integer FROM late_confirmation;

INSERT INTO harness.sweep_result
SELECT
    'expired_orphans_remaining',
    count(*)::integer
FROM attachment.reservation AS reservation
WHERE reservation.state = 'reserved'
  AND reservation.expires_at <= clock_timestamp()
  AND NOT EXISTS
      (SELECT 1 FROM notifications.notification AS notification
       WHERE notification.run_id = reservation.run_id);

DO $$
DECLARE
    v_value integer;
BEGIN
    SELECT value INTO v_value FROM harness.sweep_result WHERE name = 'swept_count';
    IF v_value <> 12 THEN
        RAISE EXCEPTION 'O sweep deveria remover 12 reservas; removeu %.', v_value;
    END IF;

    SELECT value INTO v_value
    FROM harness.sweep_result WHERE name = 'late_confirmation_updated';
    IF v_value <> 0 THEN
        RAISE EXCEPTION 'A confirmação tardia deveria atualizar zero reservas; atualizou %.', v_value;
    END IF;

    SELECT value INTO v_value
    FROM harness.sweep_result WHERE name = 'expired_orphans_remaining';
    IF v_value <> 0 THEN
        RAISE EXCEPTION 'O sweep deixou % reservas órfãs expiradas.', v_value;
    END IF;

    IF EXISTS
    (
        SELECT 1 FROM attachment.reservation
        WHERE run_id = 'C:confirmation_delayed_past_ttl'
    ) THEN
        RAISE EXCEPTION 'A reserva atrasada de C deveria ter sido removida pelo sweep literal.';
    END IF;

    IF NOT EXISTS
    (
        SELECT 1
        FROM notifications.notification AS notification
        JOIN platform.outbox AS outbox ON outbox.run_id = notification.run_id
        WHERE notification.run_id = 'C:confirmation_delayed_past_ttl'
          AND outbox.kind = 'claim-confirmation'
    ) THEN
        RAISE EXCEPTION 'O aceite e a confirmação durável de C deveriam sobreviver ao sweep.';
    END IF;
END;
$$;

SELECT
    alternative,
    failure_point,
    notification_count AS notification,
    idempotency_count AS idempotency,
    acceptance_outbox_count AS acceptance_outbox,
    confirmation_outbox_count AS confirmation_outbox,
    audit_count AS audit,
    COALESCE(reservation_state, '-') AS reservation,
    processed_count AS processed,
    CASE
        WHEN notification_count = 0 THEN 'SAFE_NO_ACCEPT'
        WHEN reservation_state = 'claimed' THEN 'SAFE_CLAIMED'
        WHEN alternative = 'C'
             AND reservation_state = 'reserved'
             AND confirmation_outbox_count = 1 THEN 'SAFE_PENDING_CONFIRM'
        ELSE 'VIOLATION_ACCEPT_WITHOUT_DURABLE_CLAIM'
    END AS verdict
FROM harness.observation
ORDER BY alternative, failure_point;

SELECT name, value
FROM harness.sweep_result
ORDER BY name;

SELECT
    notification.run_id,
    EXISTS
        (SELECT 1 FROM attachment.reservation AS reservation
         WHERE reservation.run_id = notification.run_id) AS reservation_exists,
    EXISTS
        (SELECT 1 FROM platform.outbox AS outbox
         WHERE outbox.run_id = notification.run_id
           AND outbox.kind = 'claim-confirmation') AS durable_confirmation,
    'VIOLATION_AFTER_TTL_SWEEP' AS verdict
FROM notifications.notification AS notification
WHERE notification.run_id = 'C:confirmation_delayed_past_ttl';
