using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DotAsset.Tests
{
    // A queued task whose wrapper stopped waiting is refused at claim instead of run.
    public sealed class TaskExpiryTests
    {
        [Test]
        public void ExpiresOnlyOncePastItsTime()
        {
            var task = new JObject { ["RunName"] = "EditMaterial", ["ExpiresUnix"] = 1000.5 };
            Assert.IsFalse(TaskQueue.IsExpired(task, 1000));
            Assert.IsTrue(TaskQueue.IsExpired(task, 1001));
            task["ExpiresUnix"] = 1000;
            Assert.IsTrue(TaskQueue.IsExpired(task, 1000.1), "a whole number works too");
        }

        [Test]
        public void TaskWithoutExpiryNeverExpires()
        {
            Assert.IsFalse(TaskQueue.IsExpired(new JObject { ["RunName"] = "EditMaterial" }, double.MaxValue), "an older wrapper's task");
            Assert.IsFalse(TaskQueue.IsExpired(new JObject { ["ExpiresUnix"] = "soon" }, double.MaxValue), "not a number");
        }
    }
}
