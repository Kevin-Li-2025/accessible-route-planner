import React, { useState } from "react";
import { 
  View, 
  Text, 
  TextInput, 
  TouchableOpacity, 
  StyleSheet, 
  ActivityIndicator,
  KeyboardAvoidingView,
  Platform,
  SafeAreaView,
  ScrollView,
  Alert
} from "react-native";
import { LinearGradient } from "expo-linear-gradient";
import { Ionicons } from "@expo/vector-icons";
import Animated, { FadeInUp } from "react-native-reanimated";
import { router, useLocalSearchParams } from "expo-router";
import { authService } from "@/services/auth.service";
import { ErrorMessage } from "@/components/ErrorMessage";
import { useFormAnimation } from "@/hooks/use-form-animation";

export default function ResetPasswordScreen() {
  const params = useLocalSearchParams();
  const [email, setEmail] = useState((params.email as string) || "");
  const [token, setToken] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [errorStatus, setErrorStatus] = useState<{ message: string; visible: boolean }>({
    message: "",
    visible: false,
  });

  const { shake, shakeStyle } = useFormAnimation();

  const handleReset = async () => {
    if (!email || !token || !newPassword) {
      setErrorStatus({ message: "All fields are required.", visible: true });
      shake();
      return;
    }

    if (newPassword.length < 8) {
      setErrorStatus({ message: "Password must be at least 8 characters.", visible: true });
      shake();
      return;
    }

    if (newPassword !== confirmPassword) {
      setErrorStatus({ message: "Passwords do not match.", visible: true });
      shake();
      return;
    }

    setIsSubmitting(true);
    setErrorStatus({ message: "", visible: false });

    try {
      await authService.resetPassword({ email, token, newPassword });
      
      Alert.alert(
        "Success", 
        "Password has been reset. Please log in with your new password.",
        [{ text: "OK", onPress: () => router.push("/") }]
      );
    } catch (error: any) {
      setErrorStatus({ 
        message: error.message || "Reset failed. The token may be invalid or expired.", 
        visible: true 
      });
      shake();
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <SafeAreaView style={styles.container}>
      <KeyboardAvoidingView 
        behavior={Platform.OS === "ios" ? "padding" : "height"}
        style={styles.flex}
      >
        <ScrollView contentContainerStyle={styles.scrollContent}>
          <TouchableOpacity 
            style={styles.headerBack} 
            onPress={() => router.back()}
          >
            <Ionicons name="arrow-back" size={24} color="#1E293B" />
          </TouchableOpacity>

          <View style={styles.content}>
            <Animated.View entering={FadeInUp.duration(600).delay(200)}>
              <Text style={styles.title}>New Password</Text>
              <Text style={styles.subtitle}>
                Enter the token sent to your email and choose a secure new password.
              </Text>
            </Animated.View>

            <Animated.View style={[styles.formCard, shakeStyle]}>
              <View style={styles.inputGroup}>
                <Text style={styles.inputLabel}>Email</Text>
                <View style={[styles.inputWrapper, styles.disabledInput]}>
                  <Ionicons name="mail-outline" size={20} color="#94A3B8" style={styles.inputIcon} />
                  <TextInput
                    style={[styles.input, styles.disabledText]}
                    value={email}
                    onChangeText={setEmail}
                    editable={!params.email}
                    placeholder="name@example.com"
                  />
                </View>
              </View>

              <View style={styles.inputGroup}>
                <Text style={styles.inputLabel}>Reset Token</Text>
                <View style={styles.inputWrapper}>
                  <Ionicons name="key-outline" size={20} color="#64748B" style={styles.inputIcon} />
                  <TextInput
                    style={styles.input}
                    placeholder="Paste token here"
                    value={token}
                    onChangeText={setToken}
                    autoCapitalize="none"
                  />
                </View>
              </View>

              <View style={styles.inputGroup}>
                <Text style={styles.inputLabel}>New Password</Text>
                <View style={styles.inputWrapper}>
                  <Ionicons name="lock-closed-outline" size={20} color="#64748B" style={styles.inputIcon} />
                  <TextInput
                    style={styles.input}
                    placeholder="••••••••"
                    value={newPassword}
                    onChangeText={setNewPassword}
                    secureTextEntry
                  />
                </View>
              </View>

              <View style={styles.inputGroup}>
                <Text style={styles.inputLabel}>Confirm New Password</Text>
                <View style={styles.inputWrapper}>
                  <Ionicons name="shield-checkmark-outline" size={20} color="#64748B" style={styles.inputIcon} />
                  <TextInput
                    style={styles.input}
                    placeholder="••••••••"
                    value={confirmPassword}
                    onChangeText={setConfirmPassword}
                    secureTextEntry
                  />
                </View>
                <ErrorMessage message={errorStatus.message} visible={errorStatus.visible} />
              </View>

              <TouchableOpacity 
                activeOpacity={0.85} 
                style={styles.mainButtonContainer}
                onPress={handleReset}
                disabled={isSubmitting}
              >
                <LinearGradient
                  colors={["#2563EB", "#1D4ED8"]}
                  style={styles.mainButton}
                >
                  {isSubmitting ? (
                    <ActivityIndicator color="#FFF" />
                  ) : (
                    <Text style={styles.mainButtonText}>Save & Log In</Text>
                  )}
                </LinearGradient>
              </TouchableOpacity>
            </Animated.View>
          </View>
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: "#F8FAFC",
  },
  flex: {
    flex: 1,
  },
  scrollContent: {
    flexGrow: 1,
  },
  headerBack: {
    padding: 20,
    marginTop: Platform.OS === "android" ? 40 : 10,
  },
  content: {
    paddingHorizontal: 24,
    paddingTop: 10,
  },
  title: {
    fontSize: 28,
    fontWeight: "800",
    color: "#1E293B",
    marginBottom: 8,
    letterSpacing: -0.5,
  },
  subtitle: {
    fontSize: 16,
    color: "#64748B",
    marginBottom: 24,
    lineHeight: 24,
  },
  formCard: {
    backgroundColor: "#FFFFFF",
    borderRadius: 24,
    padding: 24,
    shadowColor: "#000",
    shadowOffset: { width: 0, height: 10 },
    shadowOpacity: 0.1,
    shadowRadius: 20,
    elevation: 10,
    marginBottom: 40,
  },
  inputGroup: {
    marginBottom: 20,
  },
  inputLabel: {
    fontSize: 14,
    fontWeight: "600",
    color: "#475569",
    marginBottom: 8,
    marginLeft: 4,
  },
  inputWrapper: {
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: "#F1F5F9",
    borderRadius: 16,
    borderWidth: 1,
    borderColor: "#E2E8F0",
    paddingHorizontal: 16,
    height: 56,
  },
  disabledInput: {
    backgroundColor: "#F8FAFC",
    borderColor: "#F1F5F9",
  },
  disabledText: {
    color: "#94A3B8",
  },
  inputIcon: {
    marginRight: 12,
  },
  input: {
    flex: 1,
    fontSize: 16,
    color: "#1E293B",
  },
  mainButtonContainer: {
    marginTop: 12,
  },
  mainButton: {
    height: 56,
    borderRadius: 16,
    justifyContent: "center",
    alignItems: "center",
  },
  mainButtonText: {
    color: "#FFFFFF",
    fontSize: 16,
    fontWeight: "700",
    letterSpacing: 0.5,
  },
});
