#!/bin/sh
set -eu

awslocal s3 mb s3://notification-hub-audit-worm-dev 2>/dev/null || true

for queue in \
  core-auth \
  core-critical \
  core-transactional \
  core-operational \
  contacts-changed \
  dispatch-email-auth \
  dispatch-email-critical \
  dispatch-email-transactional \
  dispatch-email-operational \
  dispatch-push-auth \
  dispatch-push-critical \
  dispatch-push-transactional \
  dispatch-push-operational \
  dispatch-sms-auth \
  dispatch-sms-critical \
  dispatch-sms-transactional \
  dispatch-sms-operational \
  dispatch-whatsapp-auth \
  dispatch-whatsapp-critical \
  dispatch-whatsapp-transactional \
  dispatch-whatsapp-operational
do
  awslocal sqs create-queue --queue-name "$queue" >/dev/null 2>&1 || true
done
